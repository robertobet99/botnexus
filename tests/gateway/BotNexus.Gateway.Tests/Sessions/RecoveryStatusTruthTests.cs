using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tests.TestInfrastructure;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// What recovery tells a reader after a LIVE run failure must be true: the gateway did not restart,
/// and a replacement is only "queued" because admission merely enqueues it. These tests read every
/// persisted notice, banner and published activity; they assert the absence of false claims and the
/// presence of required facts, not exact wording.
/// </summary>
/// <remarks>
/// In-process component integration through the real controller or <c>conversation</c> tool and the real
/// recovery service subscribed to live lifecycle events. The startup scan is not run, so any restart
/// wording here would be a false claim by construction. Both routes recover through the run lease the
/// gateway wrote when it admitted the run, so an API kickoff (an assistant-voiced row with no sender
/// label) and a user-voiced agent message are queued the same way.
/// </remarks>
public sealed class RecoveryStatusTruthTests
{
    private const string Instruction = "Apply the scheduled change exactly once, then report the outcome.";
    private const string EffectCallId = "call-effect-1";

    // Proves: a live failure on a supported recovery input queues a replacement and says so truthfully (error stated, "queued", a banner that repeats the input, no restart or no-action claim anywhere).
    [Fact]
    public async Task ConversationToolUserMessage_LiveFailure_QueuesRecoveryAndNeverClaimsARestart()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(QueuedRecoveryScript());
        var opened = await OpenThreadAsync(harness);

        await harness.SendViaConversationToolAsync(opened.ConversationId, Instruction, speakAs: "user");
        await harness.AwaitSettledAsync(ingressTurns: 2);

        var history = (await harness.ReadSessionAsync(opened.SessionId)).GetHistorySnapshot();
        var diagnostics = await harness.DescribeAsync();
        harness.Orchestrator.AcceptedReplayPosts.Count.ShouldBe(
            1, $"fixture: a user-voiced agent message is a supported recovery input{Environment.NewLine}{diagnostics}");
        AssertNoRestartClaims(harness, history, diagnostics);

        var notice = history.StatusRows().ShouldHaveSingleItem(diagnostics).Content;
        notice.Mentions("error").ShouldBeTrue($"the notice must say the run ended with an error{Environment.NewLine}{diagnostics}");
        notice.Mentions("queued").ShouldBeTrue($"an admitted replacement is queued{Environment.NewLine}{diagnostics}");
        notice.Mentions("not queued").ShouldBeFalse($"the replacement was admitted{Environment.NewLine}{diagnostics}");
        notice.Mentions("started").ShouldBeFalse($"admission only enqueues; nothing has started{Environment.NewLine}{diagnostics}");
        harness.Activities.Items.Any(item => item.Type == GatewayActivityType.System && item.Message == notice)
            .ShouldBeTrue($"the published activity must mirror the persisted notice{Environment.NewLine}{diagnostics}");

        var banner = history.BannerRows().ShouldHaveSingleItem(diagnostics);
        banner.Content.Mentions(Instruction).ShouldBeTrue($"the banner must repeat the original input{Environment.NewLine}{diagnostics}");
        banner.Content.Mentions("verify").ShouldBeTrue($"the banner must tell the agent to verify first{Environment.NewLine}{diagnostics}");
        history.HasCrashSentinel().ShouldBeFalse($"the settled recovery leaves no sentinel behind{Environment.NewLine}{diagnostics}");
    }

    // Proves: the API route, where the kickoff is an assistant-voiced row with no sender label, is recovered through the gateway's own record of what it admitted: the replacement is queued, reported as queued with a banner, and nothing claims a restart.
    [Fact]
    public async Task ApiKickoff_LiveFailure_QueuesTheReplacementAndNeverClaimsARestart()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(ApiFailureScript());

        var receipt = await RunFailingApiKickoffAsync(harness);

        var history = (await harness.ReadSessionAsync(receipt.SessionId)).GetHistorySnapshot();
        var diagnostics = await harness.DescribeAsync();
        AssertNoRestartClaims(harness, history, diagnostics);
        harness.Orchestrator.AcceptedReplayPosts.Count.ShouldBe(1, $"the kickoff's input must be identified and replayed{Environment.NewLine}{diagnostics}");
        var notice = history.StatusRows().ShouldHaveSingleItem(diagnostics).Content;
        notice.Mentions("error").ShouldBeTrue($"a failed run must be reported as such{Environment.NewLine}{diagnostics}");
        notice.Mentions("queued").ShouldBeTrue($"a replacement was queued, and the notice must say so{Environment.NewLine}{diagnostics}");
        notice.Mentions("not queued").ShouldBeFalse($"the replacement was admitted{Environment.NewLine}{diagnostics}");
        history.BannerRows().ShouldHaveSingleItem(diagnostics);
        history.HasCrashSentinel().ShouldBeFalse($"the clean replacement leaves no sentinel behind{Environment.NewLine}{diagnostics}");
        harness.Provider.CallCount.ShouldBe(3, $"one tool call, one failing call, one replacement call{Environment.NewLine}{diagnostics}");
        harness.Effects.EffectCount.ShouldBe(1, $"the completed effect must not be repeated{Environment.NewLine}{diagnostics}");
        harness.Activities.Items.Any(item => item.Type == GatewayActivityType.System && item.Message == notice)
            .ShouldBeTrue($"the published activity must mirror the persisted notice{Environment.NewLine}{diagnostics}");
    }

    // Proves: when the replacement fails too and the input's attempt budget is spent, the second failure is reported as not queued with a next step, no further replacement is admitted, and that lease is consumed.
    [Fact]
    public async Task ConversationToolUserMessage_ReplacementFailsAgain_ReportsTheSpentBudgetAndAdmitsNothingFurther()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.Text("The thread is open."),
            ScriptStep.Fail(ScriptedLlmProvider.Rejected),
            ScriptStep.Fail(ScriptedLlmProvider.Rejected)
        ])
        { RecoveryEnabled = true, MaxAutoReplayAttempts = 1 });
        var opened = await OpenThreadAsync(harness);

        await harness.SendViaConversationToolAsync(opened.ConversationId, Instruction, speakAs: "user");
        await harness.AwaitSettledAsync(ingressTurns: 2);

        var history = (await harness.ReadSessionAsync(opened.SessionId)).GetHistorySnapshot();
        var diagnostics = await harness.DescribeAsync();
        harness.Orchestrator.AcceptedReplayPosts.Count.ShouldBe(1, $"exactly one replacement may be admitted{Environment.NewLine}{diagnostics}");
        harness.Orchestrator.ReplayPosts.Count.ShouldBe(1, $"the spent budget must not even offer a second replacement{Environment.NewLine}{diagnostics}");
        harness.Provider.CallCount.ShouldBe(3, $"open, failure, failed replacement{Environment.NewLine}{diagnostics}");
        harness.Lifecycle.Count(SessionLifecycleEventType.TerminalFailure).ShouldBe(2, diagnostics);
        AssertNoRestartClaims(harness, history, diagnostics);

        var notices = history.StatusRows();
        notices.Count.ShouldBe(2, $"each failure is reported once{Environment.NewLine}{diagnostics}");
        notices[0].Content.Mentions("queued").ShouldBeTrue(notices[0].Content);
        notices[0].Content.Mentions("not queued").ShouldBeFalse(notices[0].Content);
        notices[1].Content.Mentions("error").ShouldBeTrue(notices[1].Content);
        notices[1].Content.Mentions("not queued").ShouldBeTrue($"the second failure had no budget left{Environment.NewLine}{diagnostics}");
        notices[1].Content.Mentions("post a message").ShouldBeTrue($"the notice must say what the reader can do next{Environment.NewLine}{diagnostics}");
        history.BannerRows().Count.ShouldBe(1, $"only the one admitted replacement has a banner{Environment.NewLine}{diagnostics}");
        history.HasCrashSentinel().ShouldBeFalse(
            $"a spent budget is final: it is reported once and its lease is consumed{Environment.NewLine}{diagnostics}");
    }

    private static RecoveryIngressOptions QueuedRecoveryScript() => new(
    [
        ScriptStep.Text("The thread is open."),
        ScriptStep.Fail(ScriptedLlmProvider.Rejected),
        ScriptStep.Text("Recovered.")
    ])
    { RecoveryEnabled = true };

    // A clean API turn that opens the conversation's session on the api channel, which is what makes later
    // agent-sent messages into it eligible for live recovery.
    private static async Task<IngressReceipt> OpenThreadAsync(RecoveryIngressHarness harness)
    {
        var conversation = await harness.CreateConversationAsync();
        var opened = await harness.PostViaControllerAsync(conversation.ConversationId, "Open the thread.");
        await harness.AwaitTurnsAsync(1);
        return opened;
    }

    private static RecoveryIngressOptions ApiFailureScript() => new(
    [
        ScriptStep.ToolCall(SideEffectToolContributor.ToolName, EffectCallId),
        ScriptStep.Fail(ScriptedLlmProvider.Rejected),
        ScriptStep.Text("Recovered.")
    ])
    { RecoveryEnabled = true };

    private static async Task<IngressReceipt> RunFailingApiKickoffAsync(RecoveryIngressHarness harness)
    {
        var conversation = await harness.CreateConversationAsync();
        var receipt = await harness.PostViaControllerAsync(conversation.ConversationId, Instruction);
        await harness.AwaitSettledAsync(ingressTurns: 1);
        return receipt;
    }

    // The sentinel row is a hidden marker whose own text mentions a restart; it is not a notice.
    private static void AssertNoRestartClaims(
        RecoveryIngressHarness harness, IReadOnlyList<SessionEntry> history, string diagnostics)
    {
        foreach (var row in history.Where(entry => !entry.IsCrashSentinel && !entry.Role.Equals(MessageRole.Tool)))
        {
            row.Content.Mentions("restart").ShouldBeFalse(
                $"a persisted {row.Role} row claims a restart: {row.Content}{Environment.NewLine}{diagnostics}");
            row.Content.Mentions("no human action is required").ShouldBeFalse(
                $"a persisted {row.Role} row says no human action is required: {row.Content}{Environment.NewLine}{diagnostics}");
        }

        foreach (var activity in harness.Activities.Items
            .Where(item => item.Type is GatewayActivityType.System or GatewayActivityType.Error))
        {
            activity.Message.Mentions("restart").ShouldBeFalse(
                $"a published {activity.Type} activity claims a restart: {activity.Message}{Environment.NewLine}{diagnostics}");
            activity.Message.Mentions("no human action is required").ShouldBeFalse(
                $"a published {activity.Type} activity says no human action is required: {activity.Message}{Environment.NewLine}{diagnostics}");
        }
    }
}
