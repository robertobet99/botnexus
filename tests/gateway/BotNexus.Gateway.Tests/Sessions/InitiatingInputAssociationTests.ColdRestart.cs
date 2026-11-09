using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Tests.TestInfrastructure;

namespace BotNexus.Gateway.Tests.Sessions;

// A genuine cold restart. The first service graph is made to "crash" at a chosen save: from then on every one
// of its session writes is refused, so the session file holds exactly what a process that died at that instant
// would have left. A second graph, sharing no object with the first, then starts over the same files and runs
// its startup scan. In-process component integration like the rest of this class: it is not a real process
// boundary, but the two graphs share nothing except the files.
//
// Only the session store is crashed. The conversation store's file stays writable, so a conversation write made
// after the crash point (touching its timestamp, say) still reaches it, which a real crash would have
// prevented. Recovery reads only the conversation's participants and ownership, and nothing after the crash
// point changes those in these scenarios.
//
// The two crash windows are the two places the durable handoff can be interrupted. In both, a pending
// reservation is the only thing that survives, and the new process must queue it again exactly once without
// spending another attempt.
public sealed partial class InitiatingInputAssociationTests
{
    // Proves: after a genuine cold restart, a reservation that was saved but never posted is queued again exactly once by the new process, at the attempt it already holds, and the replacement is told about the effect that finished before the crash.
    [Fact]
    public async Task ColdRestart_ReservationSavedButNeverPosted_IsQueuedOnceByTheNewProcess()
    {
        using var disk = new DurableTestDisk();
        IngressReceipt receipt;
        await using (var first = await RecoveryIngressHarness.StartAsync(FailingApiScript() with { Durable = disk }))
        {
            // The process dies right after the reservation is durable, so the post that would follow it never happens.
            first.Saves.CrashAfterSaveThat(history => history.SentinelLeases().Any(lease => lease.State == RunLeaseState.Pending));
            first.Orchestrator.RefuseReplayPosts();
            var conversation = await first.CreateConversationAsync();

            receipt = await first.PostViaControllerAsync(conversation.ConversationId, Instruction, sender: "nightly-report");
            await first.AwaitSettledAsync(ingressTurns: 1);

            var diagnostics = await first.DescribeAsync();
            first.Saves.Crashed.ShouldBeTrue($"fixture: the run must have failed and reserved its replacement{Environment.NewLine}{diagnostics}");
            first.Saves.RefusedWrites.Count(write => write.Fenced).ShouldBe(
                1, $"fixture: the only write after the crash was recovery putting the refused post on record{Environment.NewLine}{diagnostics}");
            first.Provider.CallCount.ShouldBe(2, $"fixture: one tool call and one failing call{Environment.NewLine}{diagnostics}");
            first.Effects.EffectCount.ShouldBe(1, $"fixture: the effect finished before the crash{Environment.NewLine}{diagnostics}");
        }

        var left = (await disk.ReadSessionAsync(receipt.SessionId)).GetHistorySnapshot();
        var reservation = left.SentinelLeases().ShouldHaveSingleItem("fixture: the crash left exactly the reservation");
        reservation.State.ShouldBe(RunLeaseState.Pending, "fixture: what survived is a reservation, not a running or failed run");
        reservation.Attempt.ShouldBe(1);
        left.Count(entry => entry.Content == Instruction).ShouldBe(1, "fixture: no replacement was admitted before the crash");

        await using var second = await RestartAsync(disk);
        await AssertQueuedOnceAfterRestartAsync(second, receipt.SessionId, reservation, expectedEffects: 1, EffectCallId);
    }

    // Proves: after a genuine cold restart, a replacement that was accepted and had its row saved but never took its reservation over is queued again exactly once by the new process, at the attempt the reservation already holds, and is told about the effects of both runs that finished before the crash; and recovery saved nothing after the accepted post.
    [Fact]
    public async Task ColdRestart_ReplacementAcceptedButNeverAdopted_IsQueuedOnceByTheNewProcess_NamingEveryEarlierEffect()
    {
        const string secondEffectCallId = "call-effect-2";
        using var disk = new DurableTestDisk();
        IngressReceipt receipt;
        await using (var first = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, EffectCallId),
            ScriptStep.Fail(ScriptedLlmProvider.Rejected),
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, secondEffectCallId),
            ScriptStep.Fail(ScriptedLlmProvider.Rejected)
        ])
        { RecoveryEnabled = true, Durable = disk }))
        {
            // The process dies after the kickoff and the first replacement have failed, the second replacement has
            // been reserved and accepted, and that replacement's own row is saved: before it can take the
            // reservation over.
            first.Saves.CrashAfterSaveThat(SecondReplacementRowSavedButNotAdopted);
            var conversation = await first.CreateConversationAsync();

            receipt = await first.PostViaControllerAsync(conversation.ConversationId, Instruction, sender: "nightly-report");

            // The second replacement dies before it can begin a turn, so it ends none: only the kickoff and the
            // first replacement do. Its first refused write is the signal that it got that far.
            await first.AwaitTurnsAsync(2);
            await TestAwait.EventuallyAsync(
                () => first.Saves.RefusedWrites.Count > 0,
                "the second replacement to reach the write the crash refuses",
                timeout: Liveness);

            var diagnostics = await first.DescribeAsync();
            first.Saves.Crashed.ShouldBeTrue($"fixture: the second replacement must have been reserved and accepted{Environment.NewLine}{diagnostics}");
            first.Orchestrator.AcceptedReplayPosts.Count.ShouldBe(2, $"fixture: both replacements were accepted{Environment.NewLine}{diagnostics}");
            first.Saves.RefusedWrites.ShouldAllBe(
                write => !write.Fenced,
                $"recovery made no fenced write after the accepted post: it had nothing left to save{Environment.NewLine}{diagnostics}");
            first.Provider.CallCount.ShouldBe(4, $"fixture: two tool calls and two failing calls; the second replacement never reached the model{Environment.NewLine}{diagnostics}");
            first.Effects.EffectCount.ShouldBe(2, $"fixture: both effects finished before the crash{Environment.NewLine}{diagnostics}");
        }

        var left = (await disk.ReadSessionAsync(receipt.SessionId)).GetHistorySnapshot();
        var reservation = left.SentinelLeases().ShouldHaveSingleItem("fixture: the crash left exactly the second reservation");
        reservation.State.ShouldBe(RunLeaseState.Pending, "fixture: the replacement never took the reservation over");
        reservation.Attempt.ShouldBe(2, "fixture: two attempts were already spent before the crash");

        await using var second = await RestartAsync(disk);
        await AssertQueuedOnceAfterRestartAsync(second, receipt.SessionId, reservation, expectedEffects: 2, EffectCallId, secondEffectCallId);
    }

    // True from the moment the second replacement's own row has been saved while the reservation it was queued for is still pending, which is the last write before it takes that reservation over.
    private static bool SecondReplacementRowSavedButNotAdopted(IReadOnlyList<SessionEntry> history)
        => history.SentinelLeases().Any(lease => lease.State == RunLeaseState.Pending && lease.Attempt == 2)
            && history.Count(entry => entry.Content == Instruction) >= 3;

    // A fresh service graph over the files the first one left, started as a restarted process would be: its model
    // is the verify-first reader, and its startup scan is the only thing that admits work.
    private static Task<RecoveryIngressHarness> RestartAsync(DurableTestDisk disk)
        => RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions([ObeyVerifyFirstFraming()])
        {
            RecoveryEnabled = true,
            Durable = disk
        });

    // What a restart must achieve whichever window the crash fell in: the reservation the dead process left is
    // queued again, once, at the attempt it already holds; the replacement is told about every effect that
    // finished before and repeats none; recovery saves the reservation once and nothing after the accepted
    // post; and the restart is reported as one.
    private static async Task AssertQueuedOnceAfterRestartAsync(
        RecoveryIngressHarness restarted,
        SessionId sessionId,
        RunLease reservation,
        int expectedEffects,
        params string[] namedEffectCalls)
    {
        await restarted.Recovery.ShouldNotBeNull().StartedAsync(CancellationToken.None);
        await restarted.AwaitSettledAsync(ingressTurns: 0);

        var diagnostics = await restarted.DescribeAsync();
        var admitted = restarted.Orchestrator.AcceptedReplayPosts.ShouldHaveSingleItem(
            $"the new process must queue exactly one replacement{Environment.NewLine}{diagnostics}");
        var recovery = admitted.Message.Recovery.ShouldNotBeNull(diagnostics);
        recovery.SuccessorLeaseId.ShouldBe(
            reservation.LeaseId, $"the reservation the dead process left is the one queued again{Environment.NewLine}{diagnostics}");
        recovery.Attempt.ShouldBe(
            reservation.Attempt, $"queuing a lost reservation again spends no attempt{Environment.NewLine}{diagnostics}");

        // The rows the earlier processes wrote, read back from SQLite, pair by run: each effect is named as
        // finished, and none is offered as a call whose outcome is unknown.
        recovery.CompletedCalls.Select(call => call.ToolCallId).ToList()
            .ShouldBe([.. namedEffectCalls], $"every effect that finished before the crash is named as completed{Environment.NewLine}{diagnostics}");
        recovery.OutcomeUnknownCalls.ShouldBeEmpty($"no finished effect may be offered as one to verify{Environment.NewLine}{diagnostics}");

        restarted.Provider.CallCount.ShouldBe(1, $"the replacement runs once{Environment.NewLine}{diagnostics}");
        var replacement = restarted.Provider.Calls[0];
        AssertVerifyFirstReplacement(replacement, diagnostics);
        var prompt = replacement.FinalUserText.ShouldNotBeNull(diagnostics);
        foreach (var call in namedEffectCalls)
        {
            prompt.Contains(call, StringComparison.Ordinal)
                .ShouldBeTrue($"the replacement must be told about the effect of {call}: {prompt}{Environment.NewLine}{diagnostics}");
        }

        CountOccurrences(prompt, Instruction).ShouldBe(1, $"the original input appears once{Environment.NewLine}{diagnostics}");
        restarted.Effects.EffectCount.ShouldBe(
            expectedEffects, $"the effects of earlier processes are counted too, and none was repeated{Environment.NewLine}{diagnostics}");
        PendingLeaseSaves(restarted).ShouldBe(
            1, $"the new process saves the reservation once and nothing after the accepted post{Environment.NewLine}{diagnostics}");

        var history = (await restarted.ReadSessionAsync(sessionId)).GetHistorySnapshot();
        AssertTranscriptKeepsTheUsersWords(history, diagnostics);
        history.HasCrashSentinel().ShouldBeFalse($"the clean replacement consumes the sentinel{Environment.NewLine}{diagnostics}");
        history.StatusRows().Any(row => row.Content.Mentions("restart"))
            .ShouldBeTrue($"a genuine restart is reported as one{Environment.NewLine}{diagnostics}");

        await restarted.Recovery.StartedAsync(CancellationToken.None);
        restarted.Orchestrator.ReplayPosts.Count.ShouldBe(
            1, $"a second scan must not queue anything more{Environment.NewLine}{await restarted.DescribeAsync()}");
    }
}
