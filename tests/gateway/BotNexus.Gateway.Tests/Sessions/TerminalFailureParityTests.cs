using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Tests.TestInfrastructure;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// A failed agent run must leave the same durable evidence and publish the same signal whichever
/// branch served it. The API route (no channel adapter) runs the blocking branch; the agent-message
/// route (<c>internal</c> channel) runs the streaming branch, which is the reference behaviour.
/// </summary>
/// <remarks>
/// In-process component integration through the real controller or <c>conversation</c> tool, the real
/// orchestrator, <c>GatewayHost</c> and agent loop, with a scripted model and a synthetic tool effect.
/// The recovery service is deliberately NOT started: these tests pin what a failed run records and
/// publishes, independent of what any subscriber then does with it. Cancellation is a different
/// outcome and must never be reported as a terminal failure.
/// </remarks>
public sealed class TerminalFailureParityTests
{
    private const string Instruction = "Apply the scheduled change exactly once, then report the outcome.";
    private const string EffectCallId = "call-effect-1";

    /// <summary>How the failing model call fails; each shape takes a different core lane.</summary>
    public enum FailureShape
    {
        /// <summary>Output, then an in-band error: the core returns an error message.</summary>
        OutputThenError,

        /// <summary>An in-band error with no output: the core returns an error message.</summary>
        RejectedWithoutOutput,

        /// <summary>Quota exhaustion with no output: the core throws and converts the run to Failed.</summary>
        QuotaExhausted
    }

    // Proves: the blocking API branch records one error row, keeps the sentinel and publishes exactly one TerminalFailure (never Closed) after a durable save.
    [Theory]
    [InlineData(FailureShape.OutputThenError)]
    [InlineData(FailureShape.RejectedWithoutOutput)]
    [InlineData(FailureShape.QuotaExhausted)]
    public async Task ApiKickoff_FailureAfterEffect_RecordsOneErrorRowKeepsSentinelAndPublishesOnce(FailureShape shape)
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, EffectCallId),
            FailureStep(shape)
        ]));
        var conversation = await harness.CreateConversationAsync();

        var receipt = await harness.PostViaControllerAsync(conversation.ConversationId, Instruction);
        await harness.AwaitTurnsAsync(1);

        await AssertFailureRecordedAsync(harness, receipt, shape);
    }

    // Proves: the streaming agent-message branch leaves the same evidence, including a sentinel that survives the host's own finalization.
    [Theory]
    [InlineData(FailureShape.OutputThenError)]
    [InlineData(FailureShape.RejectedWithoutOutput)]
    [InlineData(FailureShape.QuotaExhausted)]
    public async Task ConversationToolMessage_FailureAfterEffect_MatchesBlockingOutcome(FailureShape shape)
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, EffectCallId),
            FailureStep(shape)
        ]));
        var conversation = await harness.CreateConversationAsync();

        var receipt = await harness.SendViaConversationToolAsync(conversation.ConversationId, Instruction);
        await harness.AwaitTurnsAsync(1);

        await AssertFailureRecordedAsync(harness, receipt, shape);
    }

    // Proves: a clean blocking run still consumes its sentinel and publishes nothing (blocking never publishes Closed).
    [Fact]
    public async Task ApiKickoff_CleanRun_RemovesSentinelAndPublishesNoLifecycleEvent()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, EffectCallId),
            ScriptStep.Text("The scheduled change is applied.")
        ]));
        var conversation = await harness.CreateConversationAsync();

        var receipt = await harness.PostViaControllerAsync(conversation.ConversationId, Instruction);
        await harness.AwaitTurnsAsync(1);

        var history = (await harness.ReadSessionAsync(receipt.SessionId)).GetHistorySnapshot();
        var diagnostics = await harness.DescribeAsync();
        harness.Effects.EffectCount.ShouldBe(1, diagnostics);
        history.HasCrashSentinel().ShouldBeFalse($"a clean run consumes its sentinel{Environment.NewLine}{diagnostics}");
        history.ErrorRows().ShouldBeEmpty(diagnostics);
        history.Any(entry => entry.Role.Equals(MessageRole.Assistant)
            && entry.Content == "The scheduled change is applied.").ShouldBeTrue(diagnostics);
        harness.Lifecycle.Events.ShouldBeEmpty(diagnostics);
    }

    // Proves: a clean streaming run still consumes its sentinel and publishes exactly one Closed, never a TerminalFailure.
    [Fact]
    public async Task ConversationToolMessage_CleanRun_RemovesSentinelAndPublishesClosed()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, EffectCallId),
            ScriptStep.Text("The scheduled change is applied.")
        ]));
        var conversation = await harness.CreateConversationAsync();

        var receipt = await harness.SendViaConversationToolAsync(conversation.ConversationId, Instruction);
        await harness.AwaitTurnsAsync(1);

        var history = (await harness.ReadSessionAsync(receipt.SessionId)).GetHistorySnapshot();
        var diagnostics = await harness.DescribeAsync();
        harness.Effects.EffectCount.ShouldBe(1, diagnostics);
        history.HasCrashSentinel().ShouldBeFalse($"a clean run consumes its sentinel{Environment.NewLine}{diagnostics}");
        harness.Lifecycle.Count(SessionLifecycleEventType.Closed).ShouldBe(1, diagnostics);
        harness.Lifecycle.Count(SessionLifecycleEventType.TerminalFailure).ShouldBe(0, diagnostics);
    }

    // Proves: aborting a live blocking run is cancellation, not failure: no TerminalFailure, no Closed, no error row, and the sentinel and completion are left exactly as cancellation always left them.
    [Fact]
    public async Task ApiKickoff_AbortedRun_IsNotATerminalFailure()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.Hang()
        ]));
        var conversation = await harness.CreateConversationAsync();
        var receipt = await harness.PostViaControllerAsync(conversation.ConversationId, Instruction);
        await harness.Provider.AwaitCallAsync(1);

        var handle = harness.Supervisor.GetHandle(harness.AgentId, receipt.SessionId).ShouldNotBeNull();
        await handle.AbortAsync();
        await harness.AwaitTurnsAsync(1);

        var session = await harness.ReadSessionAsync(receipt.SessionId);
        var history = session.GetHistorySnapshot();
        var diagnostics = await harness.DescribeAsync();
        harness.Lifecycle.Count(SessionLifecycleEventType.TerminalFailure).ShouldBe(0, diagnostics);
        harness.Lifecycle.Count(SessionLifecycleEventType.Closed).ShouldBe(0, diagnostics);
        history.ErrorRows().ShouldBeEmpty(diagnostics);
        history.HasCrashSentinel().ShouldBeTrue(
            $"cancellation never settles the run, so the sentinel written before it stays{Environment.NewLine}{diagnostics}");
        session.RunCompletion.ShouldBeNull($"an aborted run records no completion{Environment.NewLine}{diagnostics}");
    }

    // Proves: a failed run offers nothing to fan out on either branch, whether it failed with or without partial output, so neither the inbound message (an assistant-role row when an agent sent it) nor the partial text is delivered as the reply; the partial text stays in the transcript.
    [Theory]
    [InlineData(false, FailureShape.RejectedWithoutOutput)]
    [InlineData(true, FailureShape.RejectedWithoutOutput)]
    [InlineData(false, FailureShape.OutputThenError)]
    [InlineData(true, FailureShape.OutputThenError)]
    public async Task FailedRun_OffersNothingToFanOut_NotEvenTheInboundMessageOrPartialOutput(
        bool viaConversationTool, FailureShape shape)
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, EffectCallId),
            FailureStep(shape)
        ]));
        var conversation = await harness.CreateConversationAsync();

        var receipt = viaConversationTool
            ? await harness.SendViaConversationToolAsync(conversation.ConversationId, Instruction)
            : await harness.PostViaControllerAsync(conversation.ConversationId, Instruction);
        await harness.AwaitTurnsAsync(1);

        var diagnostics = await harness.DescribeAsync();
        var fanOut = harness.Deliverer.FanOuts.ShouldHaveSingleItem(diagnostics);
        string.IsNullOrEmpty(fanOut.Content).ShouldBeTrue(
            $"nothing may be offered for delivery after a failed run, not even the inbound message or partial output{Environment.NewLine}{diagnostics}");
        if (shape == FailureShape.OutputThenError)
        {
            (await harness.ReadSessionAsync(receipt.SessionId)).GetHistorySnapshot()
                .Any(entry => entry.Role.Equals(MessageRole.Assistant) && entry.Content.Mentions(ScriptedLlmProvider.PartialOutput))
                .ShouldBeTrue($"withheld from delivery, the partial output must still be in the transcript{Environment.NewLine}{diagnostics}");
        }
    }

    // Proves: a clean run still offers its reply to fan-out on either branch, so the failure guard above is specific to failed runs.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanRun_StillOffersItsReplyToFanOut(bool viaConversationTool)
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, EffectCallId),
            ScriptStep.Text("The scheduled change is applied.")
        ]));
        var conversation = await harness.CreateConversationAsync();

        _ = viaConversationTool
            ? await harness.SendViaConversationToolAsync(conversation.ConversationId, Instruction)
            : await harness.PostViaControllerAsync(conversation.ConversationId, Instruction);
        await harness.AwaitTurnsAsync(1);

        var diagnostics = await harness.DescribeAsync();
        var fanOut = harness.Deliverer.FanOuts.ShouldHaveSingleItem(diagnostics);
        fanOut.Content.ShouldNotBeNull(diagnostics).Mentions("The scheduled change is applied")
            .ShouldBeTrue($"the reply must be offered for delivery{Environment.NewLine}{diagnostics}");
    }

    // Proves: a clean blocking run after a failure replaces the stale Failed completion and consumes the sentinel the failed run kept, without announcing another failure.
    [Fact]
    public async Task ApiKickoff_CleanRunAfterAFailure_ReplacesTheStaleCompletionAndConsumesTheSentinel()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, EffectCallId),
            ScriptStep.Fail(ScriptedLlmProvider.Rejected),
            ScriptStep.Text("The scheduled change was already applied.")
        ]));
        var conversation = await harness.CreateConversationAsync();
        var first = await harness.PostViaControllerAsync(conversation.ConversationId, Instruction);
        await harness.AwaitTurnsAsync(1);
        var afterFailure = await harness.ReadSessionAsync(first.SessionId);
        afterFailure.RunCompletion.ShouldNotBeNull().Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)
            .ShouldBeTrue("fixture: the first run must have failed");

        var second = await harness.PostViaControllerAsync(conversation.ConversationId, "Report the outcome.");
        await harness.AwaitTurnsAsync(2);

        var diagnostics = await harness.DescribeAsync();
        second.SessionId.ShouldBe(first.SessionId, "fixture: both turns must run on one session");
        var session = await harness.ReadSessionAsync(second.SessionId);
        session.RunCompletion.ShouldNotBeNull(diagnostics).Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
            .ShouldBeTrue($"the clean run must replace the stale Failed completion{Environment.NewLine}{diagnostics}");
        session.GetHistorySnapshot().HasCrashSentinel().ShouldBeFalse(
            $"the clean run must consume the sentinel the failed run kept{Environment.NewLine}{diagnostics}");
        harness.Lifecycle.Count(SessionLifecycleEventType.TerminalFailure).ShouldBe(1, diagnostics);
        harness.Lifecycle.Count(SessionLifecycleEventType.Closed).ShouldBe(0, diagnostics);
        harness.Effects.EffectCount.ShouldBe(1, diagnostics);
    }

    // Proves: an exception that escapes before the run settles keeps its contract: an Error activity, the sentinel kept, no error row, no completion and no TerminalFailure.
    [Fact]
    public async Task ApiKickoff_InfrastructureFailureBeforeTheRun_IsNotATerminalFailure()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions([])
        {
            FailAgentCreation = true
        });
        var conversation = await harness.CreateConversationAsync();

        var receipt = await harness.PostViaControllerAsync(conversation.ConversationId, Instruction);
        await harness.AwaitTurnsAsync(1);

        var session = await harness.ReadSessionAsync(receipt.SessionId);
        var history = session.GetHistorySnapshot();
        var diagnostics = await harness.DescribeAsync();
        harness.Provider.CallCount.ShouldBe(0, $"the model must never be reached{Environment.NewLine}{diagnostics}");
        harness.Activities.Items.Any(item => item.Type == GatewayActivityType.Error)
            .ShouldBeTrue($"the escaped exception is reported as an Error activity{Environment.NewLine}{diagnostics}");
        harness.Lifecycle.Events.ShouldBeEmpty(diagnostics);
        history.ErrorRows().ShouldBeEmpty(diagnostics);
        history.HasCrashSentinel().ShouldBeTrue($"the sentinel written before the run stays{Environment.NewLine}{diagnostics}");
        session.RunCompletion.ShouldBeNull(diagnostics);
    }

    /// <summary>How the run's final fenced save goes wrong.</summary>
    public enum SaveFault
    {
        /// <summary>The store refuses the save, as it does when the session was deleted or reset mid-run.</summary>
        Rejected,

        /// <summary>The store throws while saving.</summary>
        Throws
    }

    // Proves: a failure whose final save did not persist is never announced, on either branch and either way a save can go wrong.
    [Theory]
    [InlineData(false, SaveFault.Rejected)]
    [InlineData(false, SaveFault.Throws)]
    [InlineData(true, SaveFault.Rejected)]
    [InlineData(true, SaveFault.Throws)]
    public async Task FailedRun_WhoseFinalSaveDoesNotPersist_PublishesNothing(bool viaConversationTool, SaveFault fault)
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, EffectCallId),
            ScriptStep.Fail(ScriptedLlmProvider.Rejected)
        ]));
        var conversation = await harness.CreateConversationAsync();
        if (fault == SaveFault.Rejected)
            harness.Saves.RejectNextFencedSave();
        else
            harness.Saves.FailNextFencedSave();

        _ = viaConversationTool
            ? await harness.SendViaConversationToolAsync(conversation.ConversationId, Instruction)
            : await harness.PostViaControllerAsync(conversation.ConversationId, Instruction);
        await harness.AwaitTurnsAsync(1);

        var diagnostics = await harness.DescribeAsync();
        harness.Effects.EffectCount.ShouldBe(1, $"fixture: the run itself must have happened{Environment.NewLine}{diagnostics}");
        var save = harness.Saves.FencedSaves.ShouldHaveSingleItem(
            $"fixture: the run's one final save must have been attempted, and faulted{Environment.NewLine}{diagnostics}");
        save.Persisted.ShouldBeFalse($"fixture: the faulted save must not have persisted{Environment.NewLine}{diagnostics}");
        harness.Lifecycle.Count(SessionLifecycleEventType.TerminalFailure).ShouldBe(
            0, $"a failure whose evidence is not durable must not be announced{Environment.NewLine}{diagnostics}");
        harness.Lifecycle.Count(SessionLifecycleEventType.Closed).ShouldBe(0, $"a failed run is never Closed{Environment.NewLine}{diagnostics}");
    }

    private static ScriptStep FailureStep(FailureShape shape) => shape switch
    {
        FailureShape.OutputThenError => ScriptStep.FailAfterOutput(ScriptedLlmProvider.PartialOutput, ScriptedLlmProvider.CutAfterOutput),
        FailureShape.RejectedWithoutOutput => ScriptStep.Fail(ScriptedLlmProvider.Rejected),
        FailureShape.QuotaExhausted => ScriptStep.Fail(ScriptedLlmProvider.QuotaExhausted),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
    };

    // A return-path failure carries the provider's own text; an exhausted-throw failure carries the core's
    // "Provider '<name>' failed" text, which names the provider.
    private static string DetailNeedle(FailureShape shape) => shape switch
    {
        FailureShape.OutputThenError => ScriptedLlmProvider.CutAfterOutput,
        FailureShape.RejectedWithoutOutput => ScriptedLlmProvider.Rejected,
        FailureShape.QuotaExhausted => ScriptedLlmProvider.ProviderName,
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
    };

    private static int IndexOf(IReadOnlyList<SessionEntry> history, SessionEntry entry)
    {
        for (var index = 0; index < history.Count; index++)
        {
            if (ReferenceEquals(history[index], entry))
                return index;
        }

        return -1;
    }

    private static async Task AssertFailureRecordedAsync(
        RecoveryIngressHarness harness, IngressReceipt receipt, FailureShape shape)
    {
        var session = await harness.ReadSessionAsync(receipt.SessionId);
        var history = session.GetHistorySnapshot();
        var diagnostics = await harness.DescribeAsync();

        // Fixture sanity: the effect ran once and the failing call took no retry.
        harness.Effects.EffectCount.ShouldBe(1, $"the completed effect must be applied exactly once{Environment.NewLine}{diagnostics}");
        harness.Provider.CallCount.ShouldBe(2, $"one tool call, one failing call{Environment.NewLine}{diagnostics}");

        var errorRows = history.ErrorRows();
        errorRows.Count.ShouldBe(1, $"exactly one row must record the failed run{Environment.NewLine}{diagnostics}");
        errorRows[0].Content.Mentions(DetailNeedle(shape))
            .ShouldBeTrue($"the error row must carry the failure detail{Environment.NewLine}{diagnostics}");
        history.BlankAssistantRows().ShouldBeEmpty($"a failed run must not store an empty reply{Environment.NewLine}{diagnostics}");
        session.RunCompletion.ShouldNotBeNull(diagnostics).Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)
            .ShouldBeTrue($"the run must be recorded as Failed{Environment.NewLine}{diagnostics}");

        // What the run already did stays in the transcript, under one run identity: the completed tool's
        // receipt, any output produced before the failure, and the error row all belong to the same run.
        var runRows = new List<SessionEntry>
        {
            history.ToolReceipts(EffectCallId).ShouldHaveSingleItem(
                $"the completed tool's receipt must be kept{Environment.NewLine}{diagnostics}"),
            errorRows[0]
        };
        if (shape == FailureShape.OutputThenError)
        {
            var partial = history
                .Where(entry => entry.Role.Equals(MessageRole.Assistant) && entry.Content.Mentions(ScriptedLlmProvider.PartialOutput))
                .ToList()
                .ShouldHaveSingleItem($"the output produced before the failure must be kept{Environment.NewLine}{diagnostics}");
            IndexOf(history, partial).ShouldBeLessThan(
                IndexOf(history, errorRows[0]), $"the partial output precedes the error row{Environment.NewLine}{diagnostics}");
            runRows.Add(partial);
        }

        runRows.ShouldAllBe(row => row.AgentRunId.HasValue, $"every row the run wrote must carry its run id{Environment.NewLine}{diagnostics}");
        runRows.Select(row => row.AgentRunId).Distinct().Count()
            .ShouldBe(1, $"every row the run wrote must carry the same run id{Environment.NewLine}{diagnostics}");

        var failures = harness.Lifecycle.Events
            .Where(item => item.Event.Type == SessionLifecycleEventType.TerminalFailure)
            .ToList();
        failures.Count.ShouldBe(1, $"exactly one TerminalFailure must be published{Environment.NewLine}{diagnostics}");
        harness.Lifecycle.Count(SessionLifecycleEventType.Closed).ShouldBe(0, $"a failed run is never Closed{Environment.NewLine}{diagnostics}");
        failures[0].HistoryAtPublish.ErrorRows().Count.ShouldBe(1, $"the published session must carry the error row{Environment.NewLine}{diagnostics}");
        failures[0].HadSentinelAtPublish.ShouldBeTrue($"the sentinel must be present when the failure is published{Environment.NewLine}{diagnostics}");

        // Persistence before publication, proven by the store rather than by a snapshot of the live aggregate:
        // a fenced save that completed and persisted before the event was published must already carry the
        // evidence the event announces.
        var durableSaves = harness.Saves.FencedSaves
            .Where(save => save.Persisted && save.SessionId == receipt.SessionId && save.Sequence < failures[0].Sequence)
            .ToList();
        durableSaves.ShouldNotBeEmpty($"a fenced save must have persisted before the failure was published{Environment.NewLine}{diagnostics}");
        var durableSave = durableSaves[^1];
        durableSave.HistoryAtSave.ErrorRows().Count.ShouldBe(
            1, $"the persisted transcript must carry the error row{Environment.NewLine}{diagnostics}");
        durableSave.HadSentinel.ShouldBeTrue($"the persisted transcript must keep the sentinel{Environment.NewLine}{diagnostics}");
        durableSave.RunCompletionAtSave.ShouldNotBeNull(diagnostics).Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)
            .ShouldBeTrue($"the persisted completion must be Failed{Environment.NewLine}{diagnostics}");

        // The run's lease on that sentinel records the failure in the very save that persisted the error row,
        // and still names the input the run was admitted for.
        var lease = history.SentinelLeases().ShouldHaveSingleItem(
            $"the failed run's lease must stay on its sentinel{Environment.NewLine}{diagnostics}");
        lease.State.ShouldBe(RunLeaseState.Failed, diagnostics);
        lease.Attempt.ShouldBe(0, diagnostics);
        lease.Origin.ShouldBe(lease.Input, $"an original run's origin is its own input{Environment.NewLine}{diagnostics}");
        lease.FailedRunId.ShouldBe(errorRows[0].AgentRunId, $"the lease must name the run that failed{Environment.NewLine}{diagnostics}");
        lease.FailedAt.ShouldNotBeNull(diagnostics);
        var detail = lease.FailureDetail.ShouldNotBeNull($"the lease must record why the run failed{Environment.NewLine}{diagnostics}");
        detail.Mentions(DetailNeedle(shape)).ShouldBeTrue($"the lease detail must match the error row's{Environment.NewLine}{diagnostics}");
        detail.Length.ShouldBeLessThanOrEqualTo(RunLease.MaxFailureDetailLength, diagnostics);
        var input = lease.Input.Resolve(history);
        input.Match.ShouldBe(InputRefMatch.Single, $"the lease must resolve to exactly one stored input{Environment.NewLine}{diagnostics}");
        input.Entry.ShouldNotBeNull(diagnostics).Content.Mentions(Instruction)
            .ShouldBeTrue($"the lease must name the message the run was admitted for{Environment.NewLine}{diagnostics}");
        durableSave.HistoryAtSave.SentinelLeases().ShouldHaveSingleItem(
            $"the persisted transcript must carry the failed lease with the error row{Environment.NewLine}{diagnostics}").ShouldBe(lease, diagnostics);
        failures[0].HistoryAtPublish.SentinelLeases().ShouldHaveSingleItem(
            $"the published session must carry the failed lease{Environment.NewLine}{diagnostics}").LeaseId.ShouldBe(lease.LeaseId, diagnostics);

        history.HasCrashSentinel().ShouldBeTrue(
            $"the sentinel is recovery's durable marker and must survive the host's finalization{Environment.NewLine}{diagnostics}");
    }
}
