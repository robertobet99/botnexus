using BotNexus.Agent.Core.Loop;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Tests.TestInfrastructure;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// Recovery after a failed run must replay what the RUN was started with, identified by the gateway, and
/// must never complete a side effect twice. The initiating input is an API kickoff or an agent message
/// (both stored as assistant-voiced rows with no sender), so "the latest user row" and caller-supplied
/// labels are exactly the wrong evidence: an appended note or a mid-run steer would be replayed instead.
/// </summary>
/// <remarks>
/// <para>
/// In-process component integration from the real controller and <c>conversation</c> tool, through the real
/// orchestrator, <c>GatewayHost</c>, agent loop and recovery service, to the model boundary. The model is a
/// scripted stand-in that behaves like a minimal verify-first reader: it repeats the mutation only when the
/// prompt it is shown does not name the completed call. That turns "the effect count stays at one" into a
/// behavioural proof that the warning reached the model, not just that the gateway never re-ran a tool.
/// </para>
/// <para>
/// Not proven here: how a real model obeys the framing. A restart of the whole process is driven in
/// <c>InitiatingInputAssociationTests.ColdRestart.cs</c>, where a second, fresh service graph starts over the
/// SQLite files the first left behind, for both crash windows. The durable states a restart would find, and
/// how the startup scan treats each, are also pinned at the service itself in
/// <see cref="RunFailureRecoveryNoticeTests"/>; a cold agent, rebuilt from the transcript with no tool call
/// in its context, is exercised below.
/// </para>
/// </remarks>
public sealed partial class InitiatingInputAssociationTests
{
    private const string Instruction = "Apply the scheduled change exactly once, then report the outcome.";
    private const string EffectCallId = "call-effect-1";
    private const string DecoyAppend = "DECOY-APPEND: apply a different change instead.";
    private const string SteerNote = "STEER-NOTE: also adjust the schedule.";

    private static readonly TimeSpan Liveness = TimeSpan.FromSeconds(60);

    // Proves: the real incident shape (in-band service_unavailable, no output, through the transient retry lane) recovers exactly once with verify-first context and no second mutation, and recovery saves the reservation once and nothing after the accepted post.
    [Fact]
    public async Task ApiKickoff_ServiceUnavailableAfterEffect_RecoversOnceWithVerifyFirstContext()
    {
        TransientErrorClassifier.Classify(ScriptedLlmProvider.TransientServiceUnavailable)
            .ShouldBe(ProviderFailureClass.Transient, "fixture: the scripted failure must take the real transient retry lane");
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, EffectCallId),
            .. ScriptStep.Repeat(ScriptStep.Fail(ScriptedLlmProvider.TransientServiceUnavailable), 4),
            ObeyVerifyFirstFraming()
        ])
        { RecoveryEnabled = true });
        var conversation = await harness.CreateConversationAsync();

        // The first turn includes the loop's real backoff (about 3.5 seconds) before the run is failed.
        var receipt = await harness.PostViaControllerAsync(conversation.ConversationId, Instruction, sender: "nightly-report");
        await harness.AwaitSettledAsync(ingressTurns: 1);

        var session = await harness.ReadSessionAsync(receipt.SessionId);
        var history = session.GetHistorySnapshot();
        var diagnostics = await harness.DescribeAsync();
        harness.Orchestrator.ReplayPosts.Count.ShouldBe(1, $"exactly one replacement must be admitted{Environment.NewLine}{diagnostics}");
        harness.Provider.CallCount.ShouldBe(6, $"one tool call, four attempts, one replacement call{Environment.NewLine}{diagnostics}");
        harness.Effects.EffectCount.ShouldBe(1, $"the completed effect must not be repeated{Environment.NewLine}{diagnostics}");
        AssertVerifyFirstReplacement(harness.Provider.Calls[5], diagnostics);
        AssertTranscriptKeepsTheUsersWords(history, diagnostics);

        history.ErrorRows().Count.ShouldBe(1, $"the failed run is recorded once{Environment.NewLine}{diagnostics}");
        harness.Lifecycle.Count(SessionLifecycleEventType.TerminalFailure)
            .ShouldBe(1, $"the failed run is published once{Environment.NewLine}{diagnostics}");
        history.HasCrashSentinel().ShouldBeFalse($"the clean replacement consumes the sentinel{Environment.NewLine}{diagnostics}");
        PendingLeaseSaves(harness).ShouldBe(
            1, $"recovery saves the reservation once and saves nothing after the post is accepted{Environment.NewLine}{diagnostics}");
        var recovery = harness.Orchestrator.ReplayPosts.ShouldHaveSingleItem(diagnostics).Message.Recovery.ShouldNotBeNull(diagnostics);
        recovery.CompletedCalls.ShouldHaveSingleItem(diagnostics).ToolCallId.ShouldBe(
            EffectCallId, $"the rows the run wrote pair by run: its call is named as completed{Environment.NewLine}{diagnostics}");
        recovery.OutcomeUnknownCalls.ShouldBeEmpty($"a finished call is never offered as one to verify{Environment.NewLine}{diagnostics}");
        var notice = string.Join(Environment.NewLine, history.StatusRows().Select(row => row.Content));
        notice.Mentions("queued").ShouldBeTrue($"the admitted replacement is reported as queued{Environment.NewLine}{diagnostics}");
        notice.Mentions("restart").ShouldBeFalse($"nothing restarted{Environment.NewLine}{diagnostics}");
    }

    // Proves: a note appended with wake:false and a steer injected mid-run cannot become the replayed input; recovery replays what the run was started with.
    [Fact]
    public async Task CallerLabels_AppendedDecoyAndMidRunSteer_DoNotDriveRecoveryInput()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(FailingApiScript());
        var conversation = await harness.CreateConversationAsync();
        harness.Effects.HoldNextEffect();

        var receipt = await harness.PostViaControllerAsync(conversation.ConversationId, Instruction, sender: "nightly-report");
        await harness.Effects.AwaitHeldEffectAsync();
        await harness.PostViaControllerAsync(conversation.ConversationId, DecoyAppend, wake: false, sender: "decoy-writer");
        var steer = await harness.SteerAsync(receipt, SteerNote, "steer-label");
        steer.ShouldBe(InboundDispatchStatus.Steered, "fixture: the steer must reach the live turn");
        harness.Effects.ReleaseHeldEffect();
        await harness.AwaitSettledAsync(ingressTurns: 1);

        var diagnostics = await harness.DescribeAsync();
        harness.Orchestrator.ReplayPosts.Count.ShouldBe(1, $"exactly one replacement must be admitted{Environment.NewLine}{diagnostics}");
        harness.Provider.CallCount.ShouldBe(3, $"one tool call, one failing call, one replacement call{Environment.NewLine}{diagnostics}");
        var replacement = harness.Provider.Calls[2];
        AssertVerifyFirstReplacement(replacement, diagnostics);
        var prompt = replacement.FinalUserText.ShouldNotBeNull(diagnostics);
        prompt.Contains(DecoyAppend, StringComparison.Ordinal)
            .ShouldBeFalse($"an appended note must not be replayed: {prompt}{Environment.NewLine}{diagnostics}");
        prompt.Contains(SteerNote, StringComparison.Ordinal)
            .ShouldBeFalse($"a steer must not be replayed: {prompt}{Environment.NewLine}{diagnostics}");
        harness.Effects.EffectCount.ShouldBe(1, $"the completed effect must not be repeated{Environment.NewLine}{diagnostics}");
    }

    // Proves: a stale or duplicate TerminalFailure arriving while the replacement is live admits nothing and reports nothing.
    [Fact]
    public async Task DuplicateTerminalFailure_WhileReplacementIsLive_AdmitsAtMostOnce()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, EffectCallId),
            ScriptStep.Fail(ScriptedLlmProvider.Rejected),
            ScriptStep.HoldThen(release.Task, ObeyVerifyFirstFraming())
        ])
        { RecoveryEnabled = true });
        try
        {
            var conversation = await harness.CreateConversationAsync();
            var receipt = await harness.PostViaControllerAsync(conversation.ConversationId, Instruction);
            await harness.AwaitTurnsAsync(1);
            var diagnostics = await harness.DescribeAsync();
            harness.Orchestrator.ReplayPosts.Count.ShouldBe(1, $"exactly one replacement must be admitted{Environment.NewLine}{diagnostics}");
            await harness.Provider.AwaitCallAsync(3);
            var failure = harness.Lifecycle.Events.Single(item => item.Event.Type == SessionLifecycleEventType.TerminalFailure);
            var statusRowsBefore = (await harness.ReadSessionAsync(receipt.SessionId)).GetHistorySnapshot().StatusRows().Count;

            await harness.Lifecycle.RepublishAsync(failure);

            var after = await harness.DescribeAsync();
            harness.Orchestrator.ReplayPosts.Count.ShouldBe(1, $"a stale event must not admit a second replacement{Environment.NewLine}{after}");
            harness.Provider.CallCount.ShouldBe(3, $"a stale event must not reach the model{Environment.NewLine}{after}");
            (await harness.ReadSessionAsync(receipt.SessionId)).GetHistorySnapshot().StatusRows().Count
                .ShouldBe(statusRowsBefore, $"a stale event must not report the failure again{Environment.NewLine}{after}");
        }
        finally
        {
            release.TrySetResult();
        }

        await harness.AwaitSettledAsync(ingressTurns: 1);
        harness.Orchestrator.ReplayPosts.Count.ShouldBe(1, await harness.DescribeAsync());
        harness.Effects.EffectCount.ShouldBe(1, await harness.DescribeAsync());
    }

    // Proves: a startup scan racing the live handler for the same failed run admits at most one replacement.
    [Fact]
    public async Task StartupScan_RacingTheLiveHandler_AdmitsAtMostOnce()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(FailingApiScript());
        var recovery = harness.Recovery.ShouldNotBeNull();
        var conversation = await harness.CreateConversationAsync();
        harness.Orchestrator.HoldReplayPosts();

        await harness.PostViaControllerAsync(conversation.ConversationId, Instruction);
        await TestAwait.EventuallyAsync(
            () => harness.Orchestrator.ReplayPostHeld || harness.Activities.TurnsFinished >= 1,
            "the failed run to reach a replacement admission, or to end without one",
            timeout: Liveness);
        harness.Orchestrator.ReplayPostHeld.ShouldBeTrue(
            $"a replacement must be admitted for the failed run{Environment.NewLine}{await harness.DescribeAsync()}");

        var scan = Task.Run(() => recovery.StartedAsync(CancellationToken.None));
        harness.Orchestrator.ReleaseReplayPosts();
        await scan.WaitAsync(Liveness);
        await harness.AwaitSettledAsync(ingressTurns: 1);

        var diagnostics = await harness.DescribeAsync();
        harness.Orchestrator.ReplayPosts.Count.ShouldBe(1, $"the scan and the live handler must admit one replacement between them{Environment.NewLine}{diagnostics}");
        harness.Provider.CallCount.ShouldBe(3, $"one tool call, one failing call, one replacement call{Environment.NewLine}{diagnostics}");
        harness.Effects.EffectCount.ShouldBe(1, $"the completed effect must not be repeated{Environment.NewLine}{diagnostics}");
    }

    // Proves: a startup scan that runs while a turn is live leaves that turn's marker and transcript alone.
    [Fact]
    public async Task StartupScan_WhileATurnIsLive_DoesNotTouchTheLiveRun()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, EffectCallId),
            ScriptStep.Text("The scheduled change is applied.")
        ])
        { RecoveryEnabled = true });
        var recovery = harness.Recovery.ShouldNotBeNull();
        var conversation = await harness.CreateConversationAsync();
        harness.Effects.HoldNextEffect();
        var receipt = await harness.PostViaControllerAsync(conversation.ConversationId, Instruction);
        await harness.Effects.AwaitHeldEffectAsync();

        await recovery.StartedAsync(CancellationToken.None);

        var live = (await harness.ReadSessionAsync(receipt.SessionId)).GetHistorySnapshot();
        var diagnostics = await harness.DescribeAsync();
        live.StatusRows().ShouldBeEmpty($"a live turn is not an interrupted turn{Environment.NewLine}{diagnostics}");
        live.BannerRows().ShouldBeEmpty($"a live turn is not replayed{Environment.NewLine}{diagnostics}");
        live.HasCrashSentinel().ShouldBeTrue($"the live turn's own marker must survive the scan{Environment.NewLine}{diagnostics}");
        harness.Orchestrator.ReplayPosts.ShouldBeEmpty(diagnostics);

        harness.Effects.ReleaseHeldEffect();
        await harness.AwaitSettledAsync(ingressTurns: 1);
        var settled = await harness.DescribeAsync();
        harness.Provider.CallCount.ShouldBe(2, $"the live turn must finish untouched{Environment.NewLine}{settled}");
        harness.Effects.EffectCount.ShouldBe(1, settled);
    }

    // Proves: an agent-voiced conversation-tool message into an API conversation recovers through the same association as an API kickoff.
    [Fact]
    public async Task ConversationToolMessage_IntoApiConversation_RecoversOnceWithVerifyFirstContext()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.Text("The thread is open."),
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, EffectCallId),
            ScriptStep.Fail(ScriptedLlmProvider.Rejected),
            ObeyVerifyFirstFraming()
        ])
        { RecoveryEnabled = true });
        var conversation = await harness.CreateConversationAsync();
        await harness.PostViaControllerAsync(conversation.ConversationId, "Open the thread.");
        await harness.AwaitTurnsAsync(1);

        await harness.SendViaConversationToolAsync(conversation.ConversationId, Instruction);
        await harness.AwaitSettledAsync(ingressTurns: 2);

        var diagnostics = await harness.DescribeAsync();
        harness.Orchestrator.ReplayPosts.Count.ShouldBe(1, $"exactly one replacement must be admitted{Environment.NewLine}{diagnostics}");
        harness.Provider.CallCount.ShouldBe(4, $"open, tool call, failing call, replacement call{Environment.NewLine}{diagnostics}");
        harness.Effects.EffectCount.ShouldBe(1, $"the completed effect must not be repeated{Environment.NewLine}{diagnostics}");
        AssertVerifyFirstReplacement(harness.Provider.Calls[3], diagnostics);
    }

    // Proves: a replacement that fails again is retried from the ORIGINAL input (never a copy of a copy) and the budget bounds the runs.
    [Fact]
    public async Task ReplacementThatFailsAgain_RetriesTheOriginalInputUntilTheBudgetIsSpent()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, EffectCallId),
            ScriptStep.Fail(ScriptedLlmProvider.Rejected),
            ScriptStep.Fail(ScriptedLlmProvider.Rejected),
            ScriptStep.Fail(ScriptedLlmProvider.Rejected)
        ])
        { RecoveryEnabled = true, MaxAutoReplayAttempts = 2 });
        var conversation = await harness.CreateConversationAsync();

        await harness.PostViaControllerAsync(conversation.ConversationId, Instruction);
        await harness.AwaitSettledAsync(ingressTurns: 1);

        var diagnostics = await harness.DescribeAsync();
        harness.Orchestrator.ReplayPosts.Count.ShouldBe(2, $"two replacements, then the budget is spent{Environment.NewLine}{diagnostics}");
        harness.Provider.CallCount.ShouldBe(4, $"the original run and two replacements, with no third replacement{Environment.NewLine}{diagnostics}");
        foreach (var replacement in harness.Provider.Calls.Skip(2))
        {
            var prompt = replacement.FinalUserText.ShouldNotBeNull(diagnostics);
            CountOccurrences(prompt, Instruction).ShouldBe(
                1, $"attempt {replacement.Number} must carry the original input exactly once: {prompt}{Environment.NewLine}{diagnostics}");
        }

        harness.Effects.EffectCount.ShouldBe(1, $"the completed effect must not be repeated{Environment.NewLine}{diagnostics}");
    }

    // Proves: an agent rebuilt cold from the transcript, which holds no tool call at all, still hears from the block what finished, verifies before acting and repeats nothing.
    [Fact]
    public async Task ApiKickoff_RecoveredOnAColdAgent_StillNamesTheCompletedCallAndRepeatsNothing()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(FailingApiScript());
        var conversation = await harness.CreateConversationAsync();
        harness.Orchestrator.HoldReplayPosts();

        var receipt = await harness.PostViaControllerAsync(conversation.ConversationId, Instruction, sender: "nightly-report");
        await TestAwait.EventuallyAsync(
            () => harness.Orchestrator.ReplayPostHeld,
            "the failed run to reach its replacement admission",
            timeout: Liveness);
        await harness.Supervisor.StopAsync(harness.AgentId, receipt.SessionId);
        harness.Orchestrator.ReleaseReplayPosts();
        await harness.AwaitSettledAsync(ingressTurns: 1);

        var diagnostics = await harness.DescribeAsync();
        harness.Provider.CallCount.ShouldBe(3, $"one tool call, one failing call, one replacement call{Environment.NewLine}{diagnostics}");
        var replacement = harness.Provider.Calls[2];
        replacement.ToolResultCount.ShouldBe(0, $"fixture: a rebuilt agent holds no tool result, so only the block can say what finished{Environment.NewLine}{diagnostics}");
        AssertVerifyFirstReplacement(replacement, diagnostics);
        harness.Effects.EffectCount.ShouldBe(1, $"the completed effect must not be repeated{Environment.NewLine}{diagnostics}");
    }

    // Proves: each replacement is told about the effects of EARLIER replacements too, so the whole chain's completed work is named, and the original input still appears once.
    [Fact]
    public async Task SecondReplacement_NamesTheEffectsOfTheFirstReplacementToo()
    {
        const string secondEffectCallId = "call-effect-2";
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, EffectCallId),
            ScriptStep.Fail(ScriptedLlmProvider.Rejected),
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, secondEffectCallId),
            ScriptStep.Fail(ScriptedLlmProvider.Rejected),
            ScriptStep.Text("Verified; nothing is left to do.")
        ])
        { RecoveryEnabled = true, MaxAutoReplayAttempts = 2 });
        var conversation = await harness.CreateConversationAsync();

        await harness.PostViaControllerAsync(conversation.ConversationId, Instruction);
        await harness.AwaitSettledAsync(ingressTurns: 1);

        var diagnostics = await harness.DescribeAsync();
        harness.Orchestrator.ReplayPosts.Count.ShouldBe(2, $"two replacements{Environment.NewLine}{diagnostics}");
        harness.Provider.CallCount.ShouldBe(5, $"two tool calls, two failures, one final call{Environment.NewLine}{diagnostics}");
        var prompt = harness.Provider.Calls[4].FinalUserText.ShouldNotBeNull(diagnostics);
        prompt.Contains(EffectCallId, StringComparison.Ordinal).ShouldBeTrue($"the original run's effect must be named: {prompt}{Environment.NewLine}{diagnostics}");
        prompt.Contains(secondEffectCallId, StringComparison.Ordinal).ShouldBeTrue($"the first replacement's effect must be named too: {prompt}{Environment.NewLine}{diagnostics}");
        CountOccurrences(prompt, Instruction).ShouldBe(1, $"the original input appears once{Environment.NewLine}{diagnostics}");
    }

    // Proves: a second replacement queued for a reservation that was already taken is dropped by the host, so one failure never produces two replacement runs.
    [Fact]
    public async Task DuplicateReplacementForOneReservation_RunsOnlyOnce()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, EffectCallId),
            ScriptStep.Fail(ScriptedLlmProvider.Rejected),
            ScriptStep.HoldThen(release.Task, ObeyVerifyFirstFraming()),
            ScriptStep.Text("Acknowledged.")
        ])
        { RecoveryEnabled = true });
        var conversation = await harness.CreateConversationAsync();
        var receipt = await harness.PostViaControllerAsync(conversation.ConversationId, Instruction);
        try
        {
            await harness.Provider.AwaitCallAsync(3);
            var admitted = harness.Orchestrator.AcceptedReplayPosts.ShouldHaveSingleItem().Message;

            // Unflagged, so the harness does not wait for a turn that must never happen.
            harness.Orchestrator.Post(admitted with { Metadata = new Dictionary<string, object?>() })
                .ShouldBeTrue("fixture: the duplicate reaches the queue, so the host is what must drop it");
        }
        finally
        {
            release.TrySetResult();
        }

        await harness.AwaitTurnsAsync(2);
        await harness.PostViaControllerAsync(conversation.ConversationId, "Report the outcome.");
        await harness.AwaitTurnsAsync(3);

        var diagnostics = await harness.DescribeAsync();
        var history = (await harness.ReadSessionAsync(receipt.SessionId)).GetHistorySnapshot();
        harness.Provider.CallCount.ShouldBe(4, $"tool call, failure, ONE replacement, then the barrier: the duplicate never reached the model{Environment.NewLine}{diagnostics}");
        harness.Effects.EffectCount.ShouldBe(1, $"the completed effect must not be repeated{Environment.NewLine}{diagnostics}");
        history.Count(entry => entry.Content == Instruction).ShouldBe(2, $"the kickoff and exactly one replacement copy{Environment.NewLine}{diagnostics}");
    }

    // Proves: a follow-up queued ahead of the replacement runs first without consuming the replacement's reservation, so the replacement still runs exactly once, on the attempt it was queued for, and the follow-up is neither replayed nor lost.
    [Fact]
    public async Task FollowUpQueuedAheadOfTheReplacement_RunsFirst_AndTheReplacementStillRunsOnceOnItsOwnAttempt()
    {
        const string followUp = "FOLLOW-UP: please also confirm the schedule name.";
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, EffectCallId),
            ScriptStep.Fail(ScriptedLlmProvider.Rejected),
            ScriptStep.Text("Noted; I will confirm the schedule name."),
            ObeyVerifyFirstFraming()
        ])
        { RecoveryEnabled = true });
        var conversation = await harness.CreateConversationAsync();
        harness.Effects.HoldNextEffect();

        var receipt = await harness.PostViaControllerAsync(conversation.ConversationId, Instruction, sender: "nightly-report");
        await harness.Effects.AwaitHeldEffectAsync();
        await harness.PostViaControllerAsync(conversation.ConversationId, followUp, sender: "follow-up-writer");
        harness.Effects.ReleaseHeldEffect();
        await harness.AwaitSettledAsync(ingressTurns: 2);

        var diagnostics = await harness.DescribeAsync();
        var history = (await harness.ReadSessionAsync(receipt.SessionId)).GetHistorySnapshot();
        harness.Provider.CallCount.ShouldBe(4, $"the tool call, the failure, the follow-up, then the replacement{Environment.NewLine}{diagnostics}");
        var followUpCall = harness.Provider.Calls[2].FinalUserText.ShouldNotBeNull(diagnostics);
        followUpCall.Contains(followUp, StringComparison.Ordinal)
            .ShouldBeTrue($"fixture: the follow-up must run before the replacement: {followUpCall}{Environment.NewLine}{diagnostics}");
        followUpCall.Contains(Instruction, StringComparison.Ordinal)
            .ShouldBeFalse($"the follow-up is not the interrupted input and must not carry it: {followUpCall}{Environment.NewLine}{diagnostics}");
        AssertVerifyFirstReplacement(harness.Provider.Calls[3], diagnostics);
        var replacement = harness.Orchestrator.ReplayPosts.ShouldHaveSingleItem($"exactly one replacement must be admitted{Environment.NewLine}{diagnostics}");
        replacement.Message.Recovery.ShouldNotBeNull().Attempt.ShouldBe(1, $"the failure spent one attempt, once{Environment.NewLine}{diagnostics}");
        harness.Effects.EffectCount.ShouldBe(1, $"the completed effect must not be repeated{Environment.NewLine}{diagnostics}");
        history.HasCrashSentinel().ShouldBeFalse($"the follow-up and the replacement both finished cleanly{Environment.NewLine}{diagnostics}");
        harness.Lifecycle.Count(SessionLifecycleEventType.TerminalFailure)
            .ShouldBe(1, $"only the first run failed{Environment.NewLine}{diagnostics}");
    }

    private static RecoveryIngressOptions FailingApiScript() => new(
    [
        ScriptStep.ToolCall(SideEffectToolContributor.ToolName, EffectCallId),
        ScriptStep.Fail(ScriptedLlmProvider.Rejected),
        ObeyVerifyFirstFraming()
    ])
    { RecoveryEnabled = true };

    // A minimal verify-first reader: it repeats the mutation unless the prompt names the completed call.
    private static ScriptStep ObeyVerifyFirstFraming() => ScriptStep.Respond(call =>
        call.FinalUserText is { } prompt && NamesCompletedCall(prompt)
            ? ScriptStep.Text("Verified the earlier change is in place; nothing to repeat.")
            : ScriptStep.ToolCall(SideEffectToolContributor.ToolName, "call-effect-blind-repeat"));

    private static bool NamesCompletedCall(string text)
        => text.Contains(SideEffectToolContributor.ToolName, StringComparison.Ordinal)
            || text.Contains(EffectCallId, StringComparison.Ordinal);

    private static bool IsVerifyFirst(string text)
        => new[] { "verif", "check", "inspect", "confirm" }
            .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static void AssertVerifyFirstReplacement(ProviderCall replacement, string diagnostics)
    {
        replacement.ContextContains(Instruction)
            .ShouldBeTrue($"the replacement must still know the original instruction{Environment.NewLine}{diagnostics}");
        var prompt = replacement.FinalUserText.ShouldNotBeNull(diagnostics);
        NamesCompletedCall(prompt).ShouldBeTrue(
            $"the replacement prompt must name the completed tool call: {prompt}{Environment.NewLine}{diagnostics}");
        IsVerifyFirst(prompt).ShouldBeTrue(
            $"the replacement prompt must ask the model to verify before acting: {prompt}{Environment.NewLine}{diagnostics}");
    }

    // The framing is for the model only. The transcript keeps the user's words exactly as they were written.
    private static void AssertTranscriptKeepsTheUsersWords(IReadOnlyList<SessionEntry> history, string diagnostics)
    {
        foreach (var row in history.Where(entry =>
            (entry.Role.Equals(MessageRole.User) || entry.Role.Equals(MessageRole.Assistant))
            && entry.Content.Contains(Instruction, StringComparison.Ordinal)))
        {
            row.Content.ShouldBe(Instruction, $"the transcript must not carry gateway framing{Environment.NewLine}{diagnostics}");
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;

        return count;
    }

    // The fenced saves that carried a pending reservation. Recovery makes exactly one, the reservation save.
    // A second would be a save made after the post, from a copy of the session that the replacement, already
    // running, may have outrun.
    private static int PendingLeaseSaves(RecoveryIngressHarness harness)
        => harness.Saves.FencedSaves.Count(save =>
            save.HistoryAtSave.SentinelLeases().Any(lease => lease.State == RunLeaseState.Pending));
}
