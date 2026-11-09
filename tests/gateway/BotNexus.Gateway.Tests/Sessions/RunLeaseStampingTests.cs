using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Tests.TestInfrastructure;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// Every run the gateway admits is stamped with a lease on its crash sentinel before the model is called,
/// whichever route started it, because recovery can only trust what the admitting code wrote itself.
/// </summary>
/// <remarks>
/// In-process component integration through the real controller or <c>conversation</c> tool, the real
/// orchestrator, <c>GatewayHost</c> and agent loop, with a scripted model. The model is held mid-call, so the
/// transcript read here is the durable state a crash at that instant would leave behind.
/// </remarks>
public sealed class RunLeaseStampingTests
{
    private const string Instruction = "Apply the scheduled change exactly once, then report the outcome.";

    // Proves: through the real API controller (blocking) and the real conversation tool (streaming), a run in flight has a Running attempt-0 lease on its sentinel that names the stored inbound row.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmittedRun_CarriesARunningLeaseNamingTheStoredInboundMessage_WhileTheModelIsRunning(bool viaConversationTool)
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions([ScriptStep.Hang()]));
        var conversation = await harness.CreateConversationAsync();

        var receipt = viaConversationTool
            ? await harness.SendViaConversationToolAsync(conversation.ConversationId, Instruction)
            : await harness.PostViaControllerAsync(conversation.ConversationId, Instruction);
        await harness.Provider.AwaitCallAsync(1);

        var history = (await harness.ReadSessionAsync(receipt.SessionId)).GetHistorySnapshot();
        var diagnostics = await harness.DescribeAsync();
        var lease = history.SentinelLeases().ShouldHaveSingleItem($"the run's one sentinel must carry its lease{Environment.NewLine}{diagnostics}");
        lease.State.ShouldBe(RunLeaseState.Running, diagnostics);
        lease.Attempt.ShouldBe(0, diagnostics);
        lease.Origin.ShouldBe(lease.Input, $"an original run's origin is its own input{Environment.NewLine}{diagnostics}");
        lease.FailedAt.ShouldBeNull(diagnostics);
        lease.FailureDetail.ShouldBeNull(diagnostics);
        lease.FailedRunId.ShouldBeNull(diagnostics);
        lease.InterruptedLeaseId.ShouldBeNull(diagnostics);
        var input = lease.Input.Resolve(history);
        input.Match.ShouldBe(InputRefMatch.Single, $"the lease must resolve to exactly one stored row{Environment.NewLine}{diagnostics}");
        input.Entry.ShouldNotBeNull(diagnostics).Content.Mentions(Instruction)
            .ShouldBeTrue($"the lease must name the message the run was admitted for{Environment.NewLine}{diagnostics}");
    }

    // Proves: a later run in the same conversation is admitted under its own lease that names its own input, never the failed run's.
    [Fact]
    public async Task EachAdmittedRun_GetsItsOwnLeaseNamingItsOwnInput()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.Fail(ScriptedLlmProvider.Rejected),
            ScriptStep.Hang()
        ]));
        var conversation = await harness.CreateConversationAsync();
        var first = await harness.PostViaControllerAsync(conversation.ConversationId, Instruction);
        await harness.AwaitTurnsAsync(1);
        var failedLease = (await harness.ReadSessionAsync(first.SessionId)).GetHistorySnapshot()
            .SentinelLeases().ShouldHaveSingleItem("fixture: the failed run must leave its lease behind");
        failedLease.State.ShouldBe(RunLeaseState.Failed, "fixture: the first run must have failed");

        const string secondInstruction = "Report the current state of the scheduled change.";
        var second = await harness.PostViaControllerAsync(conversation.ConversationId, secondInstruction);
        await harness.Provider.AwaitCallAsync(2);

        var history = (await harness.ReadSessionAsync(second.SessionId)).GetHistorySnapshot();
        var diagnostics = await harness.DescribeAsync();
        var lease = history.SentinelLeases().ShouldHaveSingleItem($"only the live run's lease may remain{Environment.NewLine}{diagnostics}");
        lease.State.ShouldBe(RunLeaseState.Running, diagnostics);
        lease.LeaseId.ShouldNotBe(failedLease.LeaseId, $"a new run must never reuse an earlier run's lease{Environment.NewLine}{diagnostics}");
        lease.Input.ShouldNotBe(failedLease.Input, $"the second run was admitted for a different message{Environment.NewLine}{diagnostics}");
        lease.Input.Resolve(history).Entry.ShouldNotBeNull(diagnostics).Content.Mentions(secondInstruction)
            .ShouldBeTrue($"the lease must name the second message{Environment.NewLine}{diagnostics}");
    }
}
