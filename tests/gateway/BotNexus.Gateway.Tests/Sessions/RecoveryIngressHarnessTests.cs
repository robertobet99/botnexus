using System.Reflection;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Tests.TestInfrastructure;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// Every recovery regression stands on <see cref="RecoveryIngressHarness"/>, so its own guarantees are
/// pinned here rather than inferred from a product test that happens to hang: teardown never abandons a
/// live graph, the gates a test opens and releases really work, and the settle barrier waits only for
/// turns that will actually run.
/// </summary>
public sealed class RecoveryIngressHarnessTests
{
    // Proves: teardown with a run hung in the model aborts it, completes, stops every agent handle and only then removes the home.
    [Fact]
    public async Task Teardown_WithARunHungInTheModel_AbortsItStopsTheHandlesAndRemovesTheHome()
    {
        var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions([ScriptStep.Hang()]));
        var disposed = false;
        try
        {
            var conversation = await harness.CreateConversationAsync();
            await harness.PostViaControllerAsync(conversation.ConversationId, "Apply the scheduled change.");
            await harness.Provider.AwaitCallAsync(1);
            Directory.Exists(harness.HomePath).ShouldBeTrue("fixture: the home exists while the harness is live");

            await harness.DisposeAsync();
            disposed = true;

            harness.Supervisor.GetAllInstances()
                .ShouldBeEmpty("teardown must stop the agent handles, which disposing the container never does");
            Directory.Exists(harness.HomePath).ShouldBeFalse("the home goes only after the graph is down");
        }
        finally
        {
            if (!disposed)
                await harness.DisposeAsync();
        }
    }

    // Proves: a held effect parks the run inside the tool, and releasing it once the execution is parked lets the run finish.
    [Fact]
    public async Task HeldEffect_ParksTheRunUntilReleasedThenLetsItFinish()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, "call-effect-1"),
            ScriptStep.Text("The scheduled change is applied.")
        ]));
        var conversation = await harness.CreateConversationAsync();
        harness.Effects.HoldNextEffect();

        await harness.PostViaControllerAsync(conversation.ConversationId, "Apply the scheduled change.");
        await harness.Effects.AwaitHeldEffectAsync();

        var parked = await harness.DescribeAsync();
        harness.Activities.TurnsFinished.ShouldBe(0, $"the run must stay parked inside the held tool{Environment.NewLine}{parked}");
        harness.Provider.CallCount.ShouldBe(1, parked);

        harness.Effects.ReleaseHeldEffect();
        await harness.AwaitTurnsAsync(1);

        var finished = await harness.DescribeAsync();
        harness.Effects.EffectCount.ShouldBe(1, finished);
        harness.Provider.CallCount.ShouldBe(2, finished);
    }

    // Proves: a hold released before its execution starts never parks it, so release works however early it is called.
    [Fact]
    public async Task HeldEffect_ReleasedBeforeTheExecutionStarts_DoesNotParkTheRun()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, "call-effect-1"),
            ScriptStep.Text("The scheduled change is applied.")
        ]));
        var conversation = await harness.CreateConversationAsync();
        harness.Effects.HoldNextEffect();
        harness.Effects.ReleaseHeldEffect();

        await harness.PostViaControllerAsync(conversation.ConversationId, "Apply the scheduled change.");
        await harness.AwaitTurnsAsync(1);

        harness.Effects.EffectCount.ShouldBe(1, await harness.DescribeAsync());
    }

    // Proves: a refused recovery replacement is recorded as not admitted and never waited for, so settling a refusal neither hangs nor reaches the model.
    [Fact]
    public async Task RefusedReplacement_IsRecordedAsNotAdmitted_AndSettlingDoesNotWaitForIt()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.Text("The thread is open."),
            ScriptStep.Fail(ScriptedLlmProvider.Rejected)
        ])
        { RecoveryEnabled = true });
        harness.Orchestrator.RefuseReplayPosts();
        var conversation = await harness.CreateConversationAsync();
        await harness.PostViaControllerAsync(conversation.ConversationId, "Open the thread.");
        await harness.AwaitTurnsAsync(1);

        await harness.SendViaConversationToolAsync(conversation.ConversationId, "Apply the scheduled change.", speakAs: "user");
        await harness.AwaitSettledAsync(ingressTurns: 2);

        var diagnostics = await harness.DescribeAsync();
        harness.Orchestrator.ReplayPosts.Count.ShouldBe(
            1, $"fixture: a failed user-voiced agent message makes recovery offer one replacement{Environment.NewLine}{diagnostics}");
        harness.Orchestrator.AcceptedReplayPosts.ShouldBeEmpty(diagnostics);
        harness.Provider.CallCount.ShouldBe(2, $"a refused replacement must never reach the model{Environment.NewLine}{diagnostics}");
    }

    // Proves: the recording store forwards every session store member explicitly, so wrapping the store never leaves a member to its interface default.
    [Fact]
    public void RecordingSessionStore_ForwardsEveryStoreMemberExplicitly()
    {
        var forwarders = typeof(RecordingSessionStore)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .ToList();
        var interfaceName = typeof(ISessionStore).FullName;

        var missing = typeof(ISessionStore)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(member => !forwarders.Any(candidate =>
                candidate.Name == $"{interfaceName}.{member.Name}"
                && candidate.GetParameters().Select(parameter => parameter.ParameterType)
                    .SequenceEqual(member.GetParameters().Select(parameter => parameter.ParameterType))))
            .Select(member => member.ToString())
            .ToList();

        missing.ShouldBeEmpty("a member left to its interface default would run that default against the wrapper instead of the wrapped store");
    }

    // Proves: the recording store records a completed fenced save on the shared clock and is otherwise transparent, and a rejected or throwing save persists nothing.
    [Fact]
    public async Task RecordingSessionStore_RecordsFencedSavesInOrderAndFaultsPersistNothing()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.Text("The thread is open.")
        ]));
        var conversation = await harness.CreateConversationAsync();
        var receipt = await harness.PostViaControllerAsync(conversation.ConversationId, "Open the thread.");
        await harness.AwaitTurnsAsync(1);

        var diagnostics = await harness.DescribeAsync();
        var runSaves = harness.Saves.FencedSaves;
        runSaves.ShouldNotBeEmpty($"fixture: a completed run must have made a fenced save{Environment.NewLine}{diagnostics}");
        runSaves.ShouldAllBe(save => save.Persisted && save.SessionId == receipt.SessionId, diagnostics);

        var session = await harness.ReadSessionAsync(receipt.SessionId);
        var fence = SessionWriteFence.Capture(session);
        harness.Saves.RejectNextFencedSave();
        (await harness.SessionStore.SaveAsync(session, fence)).ShouldBe(SessionSaveOutcome.Rebound, diagnostics);
        harness.Saves.FailNextFencedSave();
        await Should.ThrowAsync<IOException>(() => harness.SessionStore.SaveAsync(session, fence));
        (await harness.SessionStore.SaveAsync(session, fence)).ShouldBe(SessionSaveOutcome.Persisted, "each fault applies once");

        var saves = harness.Saves.FencedSaves;
        saves.Count.ShouldBe(runSaves.Count + 3, diagnostics);
        var rejected = saves[runSaves.Count];
        rejected.Outcome.ShouldBe(SessionSaveOutcome.Rebound, diagnostics);
        var thrown = saves[runSaves.Count + 1];
        thrown.Outcome.ShouldBeNull(diagnostics);
        thrown.Failure.ShouldBeOfType<IOException>();
        saves[runSaves.Count + 2].Persisted.ShouldBeTrue(diagnostics);
        for (var index = 1; index < saves.Count; index++)
            saves[index].Sequence.ShouldBeGreaterThan(saves[index - 1].Sequence, "completion order is sequence order");
    }

    // Proves: once the armed save has persisted, the recording store refuses every later write, fenced or not, records each refusal, still serves reads, and leaves the save that triggered the crash durable.
    [Fact]
    public async Task RecordingSessionStore_AfterTheArmedSave_RefusesEveryWriteAndStillServesReads()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.Text("The thread is open.")
        ]));
        var conversation = await harness.CreateConversationAsync();
        var receipt = await harness.PostViaControllerAsync(conversation.ConversationId, "Open the thread.");
        await harness.AwaitTurnsAsync(1);
        var session = await harness.ReadSessionAsync(receipt.SessionId);
        var fence = SessionWriteFence.Capture(session);
        harness.Saves.CrashAfterSaveThat(history => history.Any(entry => entry.Content == "marker-before-the-crash"));

        session.AddEntry(new SessionEntry { Role = MessageRole.Notification, Content = "an earlier note" });
        (await harness.SessionStore.SaveAsync(session, fence)).ShouldBe(SessionSaveOutcome.Persisted);
        harness.Saves.Crashed.ShouldBeFalse("a save that does not match must not crash the store");

        session.AddEntry(new SessionEntry { Role = MessageRole.Notification, Content = "marker-before-the-crash" });
        (await harness.SessionStore.SaveAsync(session, fence)).ShouldBe(SessionSaveOutcome.Persisted);
        harness.Saves.Crashed.ShouldBeTrue("the matching save is the last one that persists");

        await Should.ThrowAsync<IOException>(() => harness.SessionStore.SaveAsync(session, fence));
        await Should.ThrowAsync<IOException>(() => harness.SessionStore.SaveAsync(session));
        await Should.ThrowAsync<IOException>(() => harness.SessionStore.PatchMetadataAsync(
            receipt.SessionId, new Dictionary<string, object?> { ["key"] = "value" }, CancellationToken.None));

        harness.Saves.RefusedWrites
            .Select(write => $"{write.Operation}{(write.Fenced ? "/fenced" : string.Empty)}")
            .ToList()
            .ShouldBe(
                [$"{nameof(ISessionStore.SaveAsync)}/fenced", nameof(ISessionStore.SaveAsync), nameof(ISessionStore.PatchMetadataAsync)],
                "each refused write is recorded, in the order it was attempted");
        harness.Saves.FencedSaves.Count(save => save.Persisted).ShouldBeGreaterThanOrEqualTo(2, "a refused write is not a completed save");
        var read = await harness.SessionStore.GetAsync(receipt.SessionId);
        read.ShouldNotBeNull().GetHistorySnapshot().ShouldContain(
            entry => entry.Content == "marker-before-the-crash", "the save that triggered the crash stays durable and reads keep working");
    }

    // Proves: the crash can also be armed on an unfenced save, the write-ahead kind the host makes before a run, and it behaves the same way afterwards.
    [Fact]
    public async Task RecordingSessionStore_CanCrashAfterAnUnfencedSaveToo()
    {
        await using var harness = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.Text("The thread is open.")
        ]));
        var conversation = await harness.CreateConversationAsync();
        var receipt = await harness.PostViaControllerAsync(conversation.ConversationId, "Open the thread.");
        await harness.AwaitTurnsAsync(1);
        var session = await harness.ReadSessionAsync(receipt.SessionId);
        harness.Saves.CrashAfterSaveThat(history => history.Any(entry => entry.Content == "marker-before-the-crash"));

        session.AddEntry(new SessionEntry { Role = MessageRole.Notification, Content = "marker-before-the-crash" });
        await harness.SessionStore.SaveAsync(session);

        harness.Saves.Crashed.ShouldBeTrue("the matching unfenced save is the last write that persists");
        await Should.ThrowAsync<IOException>(() => harness.SessionStore.SaveAsync(session));
        harness.Saves.RefusedWrites.ShouldHaveSingleItem().Fenced.ShouldBeFalse();
    }

    // Proves: a second graph over the same durable disk finds the conversation, the session and the effects marker the first made durable, which is what a restarted process finds.
    [Fact]
    public async Task DurableDisk_LetsASecondGraphSeeWhatTheFirstMadeDurable()
    {
        using var disk = new DurableTestDisk();
        IngressReceipt receipt;
        await using (var first = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions(
        [
            ScriptStep.ToolCall(SideEffectToolContributor.ToolName, "call-effect-1"),
            ScriptStep.Text("The scheduled change is applied.")
        ])
        { Durable = disk }))
        {
            var conversation = await first.CreateConversationAsync();
            receipt = await first.PostViaControllerAsync(conversation.ConversationId, "Apply the scheduled change.");
            await first.AwaitTurnsAsync(1);
            first.Effects.EffectCount.ShouldBe(1, await first.DescribeAsync());
        }

        await using var second = await RecoveryIngressHarness.StartAsync(new RecoveryIngressOptions([]) { Durable = disk });

        var diagnostics = await second.DescribeAsync();
        (await second.ConversationStore.GetAsync(receipt.ConversationId))
            .ShouldNotBeNull($"the conversation must be durable, not just the session{Environment.NewLine}{diagnostics}");
        var history = (await second.ReadSessionAsync(receipt.SessionId)).GetHistorySnapshot();
        history.ShouldContain(
            entry => entry.Role.Equals(MessageRole.Assistant) && entry.Content.Mentions("applied"),
            $"the finished turn must be in the new graph's transcript{Environment.NewLine}{diagnostics}");
        second.Effects.EffectCount.ShouldBe(1, $"the effects marker is shared by every graph over the disk{Environment.NewLine}{diagnostics}");
        second.Provider.CallCount.ShouldBe(0, "reading what is durable must not reach the model");
    }
}
