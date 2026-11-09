using System.Runtime.CompilerServices;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Tests.TestInfrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// What recovery records after a LIVE run failure, pinned at the service itself with hand-built sessions so
/// every outcome admission can have (queued, refused, no input to replay, budget spent, no orchestrator) is
/// reachable deterministically. The startup scan is pinned alongside because the same routine serves both
/// causes and the startup texts and history order must not change.
/// </summary>
/// <remarks>
/// Unit tests: no gateway host, no model. The end-to-end behaviour through real ingress is in
/// <see cref="RecoveryStatusTruthTests"/>. Texts are asserted by the facts they must carry and the claims
/// they must not make, not by exact wording, except for the startup texts, which are an existing contract.
/// </remarks>
public sealed class RunFailureRecoveryNoticeTests
{
    private const string Instruction = "Apply the scheduled change exactly once.";
    private const string FailureDetail = "the backend rejected the request";
    private const string NoticeLead = "The previous agent run ended with an error: ";
    private const string Ellipsis = "...";
    private const int DetailBound = 240;

    // Proves: a queued replacement is reported as queued after the failure, with the banner after the notice; the failed lease is superseded by one pending reservation on the next attempt that names the same origin.
    [Fact]
    public async Task QueuedRecovery_ReportsTheFailureThenTheQueuedAttempt_AndReservesTheReplacement()
    {
        var rig = await Rig.StartAsync();
        var failedLeaseId = rig.LeaseId.ShouldNotBeNull();
        var failed = rig.Session.FindRunLease(failedLeaseId).ShouldNotBeNull();

        await rig.PublishTerminalFailureAsync();

        var history = rig.Session.GetHistorySnapshot();
        var notice = history.StatusRows().ShouldHaveSingleItem().Content;
        notice.Mentions("error").ShouldBeTrue(notice);
        notice.Mentions(FailureDetail).ShouldBeTrue($"the failure detail must be reported: {notice}");
        notice.Mentions("queued").ShouldBeTrue(notice);
        notice.Mentions("not queued").ShouldBeFalse(notice);
        notice.Mentions("attempt 1 of 2").ShouldBeTrue(notice);
        notice.Mentions("started").ShouldBeFalse($"admission only enqueues; nothing has started: {notice}");
        AssertNoRestartClaims(notice);

        var banner = history.BannerRows().ShouldHaveSingleItem();
        banner.Content.Mentions("AUTOMATIC RECOVERY 1 of 2").ShouldBeTrue(banner.Content);
        banner.Content.Mentions(Instruction).ShouldBeTrue("the original input is repeated verbatim in the banner");
        banner.Content.Mentions("verify").ShouldBeTrue("the banner must tell the agent to verify before repeating anything");
        AssertNoRestartClaims(banner.Content);
        IndexWhere(history, entry => entry.Role.Equals(MessageRole.Notification))
            .ShouldBeLessThan(IndexWhere(history, entry => entry.IsReplayBanner), "the failure is reported before the banner");

        var posted = rig.Orchestrator.Invocations.Single().Arguments.Single().ShouldBeOfType<InboundMessage>();
        posted.Content.ShouldBe(Instruction);
        posted.SpeakAs.ShouldBe(MessageRole.Assistant, "the replacement is voiced as the input was");
        posted.Sender.ShouldBe(CitizenId.Of(rig.Session.AgentId), "the sender stays the agent, never a person");
        var recovery = posted.Recovery.ShouldNotBeNull();
        recovery.Attempt.ShouldBe(1);
        recovery.MaxAttempts.ShouldBe(2);

        rig.Session.FindRunLease(failedLeaseId).ShouldBeNull("the failed lease is superseded, so a repeated signal finds nothing to act on");
        var reservation = history.SentinelLeases().ShouldHaveSingleItem();
        reservation.LeaseId.ShouldBe(recovery.SuccessorLeaseId, "the replacement names the reservation it will take over");
        reservation.State.ShouldBe(RunLeaseState.Pending);
        reservation.Attempt.ShouldBe(1);
        reservation.InterruptedLeaseId.ShouldBe(failedLeaseId);
        reservation.Origin.ShouldBe(failed.Origin, "the origin is carried over unchanged");
        rig.Session.Metadata.ShouldNotContainKey(InterruptedTurnNotificationService.MetadataKeyReplayCount,
            "a leased recovery counts on the lease and never touches the session-wide counter of lease-less sentinels");
        rig.Activities.ShouldHaveSingleItem().Message.ShouldBe(notice, "the activity mirrors the persisted notice");
    }

    // Proves: a lease that names no stored message is reported with a not-queued blocker and a next step, nothing is admitted, and that lease, being final, is consumed so it is not reported again.
    [Fact]
    public async Task NoInitiatingInput_ReportsANotQueuedBlocker_AndConsumesThatLease()
    {
        var rig = await Rig.StartAsync(withInitiatingInput: false);

        await rig.PublishTerminalFailureAsync();

        var history = rig.Session.GetHistorySnapshot();
        var notice = history.StatusRows().ShouldHaveSingleItem().Content;
        notice.Mentions("error").ShouldBeTrue(notice);
        notice.Mentions("not queued").ShouldBeTrue(notice);
        notice.Mentions("post a message").ShouldBeTrue($"the notice must say what the reader can do next: {notice}");
        AssertNoRestartClaims(notice);
        history.BannerRows().ShouldBeEmpty();
        history.HasCrashSentinel().ShouldBeFalse("a final outcome is reported once and consumes its own lease");
        rig.Orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Never);
        rig.Session.Metadata.ShouldNotContainKey(InterruptedTurnNotificationService.MetadataKeyReplayCount);
        rig.Activities.ShouldHaveSingleItem().Message.ShouldBe(notice);
    }

    // Proves: an input that has used all its attempts is reported with a not-queued blocker and a next step, nothing more is admitted, and that lease is consumed.
    [Fact]
    public async Task SpentBudget_ReportsANotQueuedBlocker_AndConsumesThatLease()
    {
        var rig = await Rig.StartAsync(leaseAttempt: 2, maxAttempts: 2);

        await rig.PublishTerminalFailureAsync();

        var history = rig.Session.GetHistorySnapshot();
        var notice = history.StatusRows().ShouldHaveSingleItem().Content;
        notice.Mentions("error").ShouldBeTrue(notice);
        notice.Mentions("not queued").ShouldBeTrue(notice);
        notice.Mentions("post a message").ShouldBeTrue(notice);
        AssertNoRestartClaims(notice);
        history.BannerRows().ShouldBeEmpty();
        history.HasCrashSentinel().ShouldBeFalse("a final outcome is reported once and consumes its own lease");
        rig.Orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Never);
    }

    // Proves: a refused admission is reported as not queued and leaves the original lease and the budget exactly as they were, and a later accepted attempt is reported as queued with its banner and one attempt.
    [Fact]
    public async Task RefusedAdmission_ReportsNotQueued_RestoresTheLease_AndALaterAcceptedAttemptIsReportedQueued()
    {
        var rig = await Rig.StartAsync(postAccepted: false);
        var failedLeaseId = rig.LeaseId.ShouldNotBeNull();
        var failed = rig.Session.FindRunLease(failedLeaseId).ShouldNotBeNull();

        await rig.PublishTerminalFailureAsync();

        var refused = rig.Session.GetHistorySnapshot().StatusRows().ShouldHaveSingleItem().Content;
        refused.Mentions("error").ShouldBeTrue(refused);
        refused.Mentions("not queued").ShouldBeTrue(refused);
        AssertNoRestartClaims(refused);
        rig.Session.FindRunLease(failedLeaseId).ShouldBe(failed, "the refused reservation is undone: the same failed lease, the same attempt");
        rig.Session.GetHistorySnapshot().SentinelLeases().ShouldHaveSingleItem().State.ShouldBe(RunLeaseState.Failed);
        rig.Session.GetHistorySnapshot().BannerRows().ShouldBeEmpty();
        rig.Session.Metadata.ShouldNotContainKey(InterruptedTurnNotificationService.MetadataKeyReplayCount);

        rig.Orchestrator.Setup(o => o.Post(It.IsAny<InboundMessage>())).Returns(true);
        await rig.PublishTerminalFailureAsync();

        var history = rig.Session.GetHistorySnapshot();
        var rows = history.StatusRows();
        rows.Count.ShouldBe(2, "each attempt reports what admission did at that moment");
        rows[0].Content.ShouldBe(refused, "an earlier report is never rewritten");
        rows[1].Content.Mentions("queued").ShouldBeTrue(rows[1].Content);
        rows[1].Content.Mentions("not queued").ShouldBeFalse(rows[1].Content);
        AssertNoRestartClaims(rows[1].Content);
        history.BannerRows().ShouldHaveSingleItem();
        history.SentinelLeases().ShouldHaveSingleItem().Attempt.ShouldBe(1, "the budget was spent once, by the accepted attempt");
        rig.Session.Metadata.ShouldNotContainKey(InterruptedTurnNotificationService.MetadataKeyReplayCount);
        rig.Orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Exactly(2));
        rig.Activities.Count.ShouldBe(2);
        rig.Activities[0].Message.ShouldBe(refused);
        rig.Activities[1].Message.ShouldBe(rows[1].Content);
    }

    // Proves: with no orchestrator the failure is reported with a not-queued reason and a next step, and the lease is kept so a later boot that has one can still recover it.
    [Fact]
    public async Task NoOrchestrator_ReportsRecoveryIsNotAvailable_AndKeepsTheLeaseForAnotherBoot()
    {
        var rig = await Rig.StartAsync(withOrchestrator: false);

        await rig.PublishTerminalFailureAsync();

        var history = rig.Session.GetHistorySnapshot();
        var notice = history.StatusRows().ShouldHaveSingleItem().Content;
        notice.Mentions("not queued").ShouldBeTrue(notice);
        notice.Mentions("not available").ShouldBeTrue(notice);
        notice.Mentions("post a message").ShouldBeTrue(notice);
        AssertNoRestartClaims(notice);
        history.SentinelLeases().ShouldHaveSingleItem().State.ShouldBe(RunLeaseState.Failed, "nothing was consumed, so a boot that has an orchestrator can still recover it");

        await rig.PublishTerminalFailureAsync();

        rig.Session.GetHistorySnapshot().StatusRows().ShouldHaveSingleItem("the same outcome for the same lease is recorded once");
        rig.Activities.ShouldHaveSingleItem("a repeated report is not announced again");
    }

    // Proves: a run that supplied no detail still reports that it ended with an error, without inventing a detail.
    [Fact]
    public async Task MissingFailureDetail_StillStatesThatTheRunEndedWithAnError()
    {
        var rig = await Rig.StartAsync(failureDetail: null);

        await rig.PublishTerminalFailureAsync();

        var notice = rig.Session.GetHistorySnapshot().StatusRows().ShouldHaveSingleItem().Content;
        notice.Mentions("ended with an error").ShouldBeTrue(notice);
        notice.Mentions("queued").ShouldBeTrue(notice);
        notice.Mentions("null").ShouldBeFalse(notice);
        AssertNoRestartClaims(notice);
    }

    // Proves: a long multi-line failure detail is bounded to one short line, so a provider's raw error text cannot flood the transcript.
    [Fact]
    public async Task LongMultilineFailureDetail_IsBoundedToOneLine()
    {
        var rig = await Rig.StartAsync(failureDetail: "first line\nsecond line\r\n\tthird line " + new string('x', 1000));

        await rig.PublishTerminalFailureAsync();

        var notice = rig.Session.GetHistorySnapshot().StatusRows().ShouldHaveSingleItem().Content;
        notice.Mentions("first line second line third line").ShouldBeTrue(notice);
        notice.ShouldNotContain("\n");
        notice.ShouldNotContain("\r");
        notice.ShouldNotContain("\t");
        notice.Length.ShouldBeLessThan(600, "the failure detail must be bounded");
        notice.Mentions("queued").ShouldBeTrue("the admission result still follows the bounded detail");
    }

    // Proves: at and around the 240-character bound the detail, with its ending, never exceeds 240: kept whole while it fits, and cut with an ellipsis the moment its ending would not.
    [Theory]
    [InlineData(1, false)]
    [InlineData(238, false)]
    [InlineData(239, false)]
    [InlineData(240, true)]
    [InlineData(241, true)]
    [InlineData(1000, true)]
    public async Task FailureDetail_AroundTheBound_NeverExceedsItWithItsEnding(int detailLength, bool expectCut)
    {
        var rig = await Rig.StartAsync(failureDetail: new string('x', detailLength));

        await rig.PublishTerminalFailureAsync();

        var detail = DetailOf(rig.Session.GetHistorySnapshot().StatusRows().ShouldHaveSingleItem().Content);
        detail.Length.ShouldBeLessThanOrEqualTo(DetailBound, detail);
        if (expectCut)
            detail.ShouldBe(new string('x', DetailBound - Ellipsis.Length) + Ellipsis, "a cut detail fills the bound exactly");
        else
            detail.ShouldBe(new string('x', detailLength) + ".", "a detail that fits is kept whole, plus its ending");
    }

    // Proves: a detail that already ends like a sentence gets no second ending, and is cut only once it is over the bound.
    [Theory]
    [InlineData(239, false)]
    [InlineData(240, true)]
    public async Task FailureDetail_ThatAlreadyEndsLikeASentence_IsNotGivenASecondEnding(int bodyLength, bool expectCut)
    {
        var detailText = new string('x', bodyLength) + "?";
        var rig = await Rig.StartAsync(failureDetail: detailText);

        await rig.PublishTerminalFailureAsync();

        var detail = DetailOf(rig.Session.GetHistorySnapshot().StatusRows().ShouldHaveSingleItem().Content);
        detail.Length.ShouldBeLessThanOrEqualTo(DetailBound, detail);
        detail.ShouldBe(expectCut ? new string('x', DetailBound - Ellipsis.Length) + Ellipsis : detailText);
    }

    // Proves: a live failure outside the agent-only API route (a human participant, or another channel) is left to its owners: nothing recorded, announced or admitted.
    [Theory]
    [InlineData(true, "api")]
    [InlineData(false, "internal")]
    public async Task UnresolvedFailure_OutsideTheAgentOnlyApiRoute_IsLeftAlone(bool hasHuman, string channel)
    {
        var rig = await Rig.StartAsync(hasHuman: hasHuman, channelType: ChannelKey.From(channel));

        await rig.PublishTerminalFailureAsync();

        rig.Session.GetHistorySnapshot().StatusRows().ShouldBeEmpty();
        rig.Session.History.ShouldContain(entry => entry.IsCrashSentinel);
        rig.Orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Never);
        rig.Activities.ShouldBeEmpty();
    }

    // Proves: a save that does not persist leaves nothing behind: no row, no reservation, no post and no announcement, and the lease is exactly the failed lease it was, so a retry starts from what is really stored.
    [Fact]
    public async Task PersistenceFailure_PostsNothingAnnouncesNothingAndRestoresTheLease()
    {
        var rig = await Rig.StartAsync();
        var failedLeaseId = rig.LeaseId.ShouldNotBeNull();
        var failed = rig.Session.FindRunLease(failedLeaseId).ShouldNotBeNull();
        var rowsBefore = rig.Session.GetHistorySnapshot().Count;
        rig.Store.Setup(store => store.SaveAsync(
                It.IsAny<GatewaySession>(), It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("synthetic persistence failure"));

        await rig.PublishTerminalFailureAsync();

        rig.Store.Verify(
            store => store.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()),
            Times.Once);
        rig.Orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Never, "nothing is posted before the reservation is durable");
        rig.Session.GetHistorySnapshot().Count.ShouldBe(rowsBefore);
        rig.Session.FindRunLease(failedLeaseId).ShouldBe(failed);
        rig.Activities.ShouldBeEmpty();
    }

    // Proves: the startup scan keeps its restart wording, its notice-before-banner history order and its banner header for a sentinel that carries no lease, because a restart notice is true whatever admission decides.
    [Fact]
    public async Task StartupScan_ForALeaselessSentinel_KeepsTheRestartTextsAndTheNoticeBeforeBannerOrder()
    {
        var rig = await Rig.StartAsync(leaseState: null);

        await rig.Service.StartedAsync(CancellationToken.None);

        var history = rig.Session.GetHistorySnapshot();
        var notice = history.StatusRows().ShouldHaveSingleItem().Content;
        notice.Mentions("gateway was restarted").ShouldBeTrue(notice);
        notice.Mentions("no human action is required").ShouldBeTrue(notice);
        var banner = history.BannerRows().ShouldHaveSingleItem();
        banner.Content.ShouldStartWith("[PLATFORM RESTART - AUTOMATIC REPLAY 1 of 2]");
        banner.Content.Mentions(Instruction).ShouldBeTrue(banner.Content);
        IndexWhere(history, entry => entry.Role.Equals(MessageRole.Notification))
            .ShouldBeLessThan(IndexWhere(history, entry => entry.IsReplayBanner), "the startup notice precedes the banner");
        history.HasCrashSentinel().ShouldBeFalse();
        rig.Session.Metadata[InterruptedTurnNotificationService.MetadataKeyReplayCount].ShouldBe(1);
        rig.Activities.ShouldHaveSingleItem().Message.ShouldBe($"{notice} (auto-replaying)");
    }

    // Proves: a repeated signal for a failure whose outcome was already persisted adds no row, no post and no announcement, because its lease is gone.
    [Fact]
    public async Task DuplicateEvent_AfterTheOutcomeWasPersisted_AddsNothing()
    {
        var rig = await Rig.StartAsync();
        await rig.PublishTerminalFailureAsync();
        var rowsAfterFirst = rig.Session.GetHistorySnapshot().Count;

        await rig.PublishTerminalFailureAsync();

        rig.Session.GetHistorySnapshot().Count.ShouldBe(rowsAfterFirst);
        rig.Orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Once);
        rig.Activities.ShouldHaveSingleItem();
        rig.Session.GetHistorySnapshot().SentinelLeases().ShouldHaveSingleItem().Attempt.ShouldBe(1, "a repeat must not spend another attempt");
        rig.Session.Metadata.ShouldNotContainKey(InterruptedTurnNotificationService.MetadataKeyReplayCount);
    }

    // Proves: a leased recovery counts on its own lease and never spends the session-wide counter that sentinels without a lease are refused on, so a leftover lease-less sentinel in the same session is still replayed at the next boot.
    [Fact]
    public async Task LeasedRecovery_DoesNotSpendTheCounterOfLeaselessSentinels_SoALeftoverOneIsStillReplayed()
    {
        const string leftoverRequest = "A request from before run leases existed.";
        var rig = await Rig.StartAsync(maxAttempts: 1);
        rig.Session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = leftoverRequest, Timestamp = DateTimeOffset.UtcNow });
        rig.Session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "[agent turn in progress]", IsCrashSentinel = true });
        await rig.PublishTerminalFailureAsync();
        var reservation = rig.Session.GetHistorySnapshot().SentinelLeases().ShouldHaveSingleItem();
        rig.Session.TryRemoveCrashSentinel(reservation.LeaseId, out _).ShouldBeTrue("fixture: the replacement ran and finished");

        await rig.Service.StartedAsync(CancellationToken.None);

        rig.Orchestrator.Verify(
            o => o.Post(It.Is<InboundMessage>(message => message.Content == leftoverRequest)),
            Times.Once,
            "the leftover sentinel is within its own budget because the leased recovery spent none of it");
    }

    // Proves: an event naming a lease that is not on the session (a stale signal for a run that was already dealt with) touches nothing.
    [Fact]
    public async Task EventNamingALeaseThatIsNotOnTheSession_TouchesNothing()
    {
        var rig = await Rig.StartAsync();
        var before = rig.Session.GetHistorySnapshot();

        await rig.Lifecycle.PublishAsync(new SessionLifecycleEvent(
            rig.Session.SessionId.Value, rig.Session.AgentId.Value, SessionLifecycleEventType.TerminalFailure, rig.Session, RunLeaseId.Create()));

        rig.Session.GetHistorySnapshot().ShouldBe(before);
        rig.Orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Never);
        rig.Activities.ShouldBeEmpty();
    }

    // Proves: an event without a lease identity cannot be acted on race-safely, so it is ignored.
    [Fact]
    public async Task EventWithoutALeaseIdentity_IsIgnored()
    {
        var rig = await Rig.StartAsync();
        var before = rig.Session.GetHistorySnapshot();

        await rig.Lifecycle.PublishAsync(new SessionLifecycleEvent(
            rig.Session.SessionId.Value, rig.Session.AgentId.Value, SessionLifecycleEventType.TerminalFailure, rig.Session));

        rig.Session.GetHistorySnapshot().ShouldBe(before);
        rig.Orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Never);
    }

    // Proves: recovering a failed run never touches another run's sentinel on the same session: the later run's lease is left exactly as it was.
    [Fact]
    public async Task RecoveringOneLease_LeavesALaterRunsSentinelUntouched()
    {
        var rig = await Rig.StartAsync();
        var followUp = new SessionEntry { Role = MessageRole.User, Content = "A later instruction.", Timestamp = DateTimeOffset.UtcNow };
        rig.Session.AddEntry(followUp);
        var laterLease = RunLease.Admit(InputRef.For(followUp));
        rig.Session.AddEntry(new SessionEntry
        {
            Role = MessageRole.System,
            Content = "[agent turn in progress - gateway restarted if visible]",
            IsCrashSentinel = true,
            RunLease = laterLease
        });

        await rig.PublishTerminalFailureAsync();

        var leases = rig.Session.GetHistorySnapshot().SentinelLeases();
        leases.Count.ShouldBe(2);
        leases[0].State.ShouldBe(RunLeaseState.Pending, "the failed run's lease was handled");
        leases[1].ShouldBe(laterLease, "the later run's lease is exactly as it was");
    }

    // Proves: a stale event for a failed earlier run, arriving while a later run holds the session, never touches the later run's lease and queues nothing for it.
    [Fact]
    public async Task StaleEventForAnEarlierRun_WhileALaterRunIsLive_TouchesNothing()
    {
        var rig = await Rig.StartAsync();
        var staleLeaseId = rig.LeaseId.ShouldNotBeNull();
        await rig.PublishTerminalFailureAsync();
        rig.Session.TryRemoveCrashSentinel(rig.Session.GetHistorySnapshot().SentinelLeases().Single().LeaseId, out _);
        var laterInput = new SessionEntry { Role = MessageRole.User, Content = "A later instruction.", Timestamp = DateTimeOffset.UtcNow };
        rig.Session.AddEntry(laterInput);
        var laterLease = RunLease.Admit(InputRef.For(laterInput));
        rig.Session.AddEntry(new SessionEntry
        {
            Role = MessageRole.System,
            Content = "[agent turn in progress - gateway restarted if visible]",
            IsCrashSentinel = true,
            RunLease = laterLease
        });
        var before = rig.Session.GetHistorySnapshot();

        await rig.Lifecycle.PublishAsync(new SessionLifecycleEvent(
            rig.Session.SessionId.Value, rig.Session.AgentId.Value, SessionLifecycleEventType.TerminalFailure, rig.Session, staleLeaseId));

        rig.Session.GetHistorySnapshot().ShouldBe(before);
        rig.Session.FindRunLease(laterLease.LeaseId).ShouldBe(laterLease);
        rig.Orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Once, "only the first event queued anything");
    }

    // Proves: a replacement that fails in turn is replayed from the ORIGINAL input on the next attempt, never from a copy of a copy, and its post names the next attempt.
    [Fact]
    public async Task ReplacementThatFailedInTurn_IsReplayedFromTheOriginalInputOnTheNextAttempt()
    {
        var rig = await Rig.StartAsync(leaseAttempt: 1, inputIsAReplacement: true);

        await rig.PublishTerminalFailureAsync();

        var posted = rig.Orchestrator.Invocations.Single().Arguments.Single().ShouldBeOfType<InboundMessage>();
        posted.Content.ShouldBe(Instruction);
        posted.Recovery.ShouldNotBeNull().Attempt.ShouldBe(2);
        var reservation = rig.Session.GetHistorySnapshot().SentinelLeases().ShouldHaveSingleItem();
        reservation.Attempt.ShouldBe(2);
        reservation.Origin.ShouldBe(InputRef.For(rig.Session.GetHistorySnapshot().First(entry => !entry.IsCrashSentinel)),
            "the origin is the first stored input of the chain, whichever attempt failed");
    }

    // Proves: what the interrupted work is known to have done reaches the replacement as typed evidence, with finished calls kept apart from calls whose outcome is unknown.
    [Fact]
    public async Task PostedReplacement_CarriesCompletedAndOutcomeUnknownToolCallsAsEvidence()
    {
        var rig = await Rig.StartAsync();
        rig.Session.AddEntries(
        [
            new SessionEntry { Role = MessageRole.Tool, Content = string.Empty, ToolName = "update_record", ToolCallId = "call-done", ToolArgs = "{\"id\":1}", Kind = MessageKind.ToolStart },
            new SessionEntry { Role = MessageRole.Tool, Content = "updated", ToolName = "update_record", ToolCallId = "call-done", ToolArgs = "{\"id\":1}", Kind = MessageKind.ToolResult },
            new SessionEntry { Role = MessageRole.Tool, Content = string.Empty, ToolName = "send_report", ToolCallId = "call-unknown", ToolArgs = "{}", Kind = MessageKind.ToolStart }
        ]);

        await rig.PublishTerminalFailureAsync();

        var posted = rig.Orchestrator.Invocations.Single().Arguments.Single().ShouldBeOfType<InboundMessage>();
        var recovery = posted.Recovery.ShouldNotBeNull();
        var completed = recovery.CompletedCalls.ShouldHaveSingleItem();
        completed.ToolCallId.ShouldBe("call-done");
        completed.ToolName.ShouldBe("update_record");
        var unknown = recovery.OutcomeUnknownCalls.ShouldHaveSingleItem();
        unknown.ToolCallId.ShouldBe("call-unknown");
        posted.Metadata["outcomeUnknownToolCallIds"].ShouldBeOfType<string[]>().ShouldBe(new[] { "call-unknown" });
    }

    // Proves: a tool call whose result was an error reaches the replacement as outcome-unknown, never as completed work, and the banner names it for verification.
    [Fact]
    public async Task PostedReplacement_ListsAnErroredToolResultAsOutcomeUnknown_NeverAsCompleted()
    {
        var rig = await Rig.StartAsync();
        rig.Session.AddEntries(
        [
            new SessionEntry { Role = MessageRole.Tool, Content = "updated", ToolName = "update_record", ToolCallId = "call-done", ToolArgs = "{\"id\":1}", Kind = MessageKind.ToolResult },
            new SessionEntry
            {
                Role = MessageRole.Tool,
                Content = "Tool 'send_report' failed: timed out after the request was sent.",
                ToolName = "send_report",
                ToolCallId = "call-errored",
                ToolArgs = "{}",
                ToolIsError = true,
                Kind = MessageKind.ToolResult
            }
        ]);

        await rig.PublishTerminalFailureAsync();

        var posted = rig.Orchestrator.Invocations.Single().Arguments.Single().ShouldBeOfType<InboundMessage>();
        var recovery = posted.Recovery.ShouldNotBeNull();
        recovery.CompletedCalls.ShouldHaveSingleItem().ToolCallId.ShouldBe("call-done");
        recovery.OutcomeUnknownCalls.ShouldHaveSingleItem().ToolCallId.ShouldBe("call-errored");
        posted.Metadata["outcomeUnknownToolCallIds"].ShouldBeOfType<string[]>().ShouldBe(new[] { "call-errored" });
        rig.Session.GetHistorySnapshot().BannerRows().ShouldHaveSingleItem().Content.ShouldContain("call-errored");
    }

    // Proves: when two runs of the chain reuse one call id, the replacement is told both calls apart: the earlier run's unresolved one to verify, with its own arguments, and the later run's finished one not to repeat, with its own; the id is named once for the banner and the metadata.
    [Fact]
    public async Task PostedReplacement_KeepsTwoRunsCallsApart_WhenTheyReuseOneCallId()
    {
        var rig = await Rig.StartAsync();
        var earlierRun = AgentRunId.From("run-earlier");
        var laterRun = AgentRunId.From("run-later");
        rig.Session.AddEntries(
        [
            new SessionEntry { Role = MessageRole.Tool, Content = string.Empty, ToolName = "update_record", ToolCallId = "call-1", ToolArgs = "{\"id\":1}", Kind = MessageKind.ToolStart, AgentRunId = earlierRun },
            new SessionEntry { Role = MessageRole.Tool, Content = string.Empty, ToolName = "update_record", ToolCallId = "call-1", ToolArgs = "{\"id\":2}", Kind = MessageKind.ToolStart, AgentRunId = laterRun },
            new SessionEntry { Role = MessageRole.Tool, Content = "updated", ToolName = "update_record", ToolCallId = "call-1", ToolArgs = "{\"id\":2}", Kind = MessageKind.ToolResult, AgentRunId = laterRun }
        ]);

        await rig.PublishTerminalFailureAsync();

        var posted = rig.Orchestrator.Invocations.Single().Arguments.Single().ShouldBeOfType<InboundMessage>();
        var recovery = posted.Recovery.ShouldNotBeNull();
        recovery.CompletedCalls.ShouldHaveSingleItem().Arguments.ShouldBe("{\"id\":2}", "the later run's finished call, with its own arguments");
        recovery.OutcomeUnknownCalls.ShouldHaveSingleItem().Arguments.ShouldBe("{\"id\":1}", "the earlier run's unresolved call, with its own arguments");
        posted.Metadata["outcomeUnknownToolCallIds"].ShouldBeOfType<string[]>().ShouldBe(new[] { "call-1" });
        var banner = rig.Session.GetHistorySnapshot().BannerRows().ShouldHaveSingleItem().Content;
        banner.Split("call-1").Length.ShouldBe(2, "the unresolved call is named once in the banner");
    }

    // Proves: an accepted live recovery makes exactly one save, before the post, and that save already carries the pending reservation, the queued notice and the banner; nothing is saved once the post is accepted.
    [Fact]
    public async Task AcceptedLiveRecovery_SavesOnceBeforeThePost_AndNothingAfterIt()
    {
        var rig = await Rig.StartAsync();

        await rig.PublishTerminalFailureAsync();

        rig.Events.ShouldBe(["save", "post"], "the reservation and its report are saved, then posted; nothing follows an accepted post");
        var saved = rig.SavedHistories.ShouldHaveSingleItem();
        saved.SentinelLeases().ShouldHaveSingleItem().State.ShouldBe(RunLeaseState.Pending);
        var notice = saved.StatusRows().ShouldHaveSingleItem().Content;
        notice.Mentions("queued").ShouldBeTrue(notice);
        saved.BannerRows().ShouldHaveSingleItem();
    }

    // Proves: an accepted startup recovery makes the same single save before the post, with the restart notice and the banner already in it, and saves nothing after the accepted post.
    [Fact]
    public async Task AcceptedStartupRecovery_SavesOnceBeforeThePost_WithTheRestartNoticeAndBannerInThatSave()
    {
        var rig = await Rig.StartAsync(leaseState: RunLeaseState.Running);

        await rig.Service.StartedAsync(CancellationToken.None);

        rig.Events.ShouldBe(["save", "post"], "the reservation and its report are saved, then posted; nothing follows an accepted post");
        var saved = rig.SavedHistories.ShouldHaveSingleItem();
        saved.StatusRows().ShouldHaveSingleItem().Content.ShouldBe(InterruptedTurnNotificationService.AgentOnlyNotificationContent);
        saved.BannerRows().ShouldHaveSingleItem().Content.ShouldStartWith("[PLATFORM RESTART - AUTOMATIC REPLAY 1 of 2]");
        saved.SentinelLeases().ShouldHaveSingleItem().State.ShouldBe(RunLeaseState.Pending);
    }

    // Proves: a refused live admission puts everything back in ONE further save: the failed lease is restored, the optimistic queued notice and banner are gone, and exactly one not-queued report is written.
    [Fact]
    public async Task RefusedLiveRecovery_RestoresInOneSave_WithoutTheOptimisticReport()
    {
        var rig = await Rig.StartAsync(postAccepted: false);
        var failed = rig.Session.FindRunLease(rig.LeaseId.ShouldNotBeNull()).ShouldNotBeNull();

        await rig.PublishTerminalFailureAsync();

        rig.Events.ShouldBe(["save", "post", "save"], "reserve, post, then one restore save");
        var reservation = rig.SavedHistories[0];
        reservation.SentinelLeases().ShouldHaveSingleItem().State.ShouldBe(RunLeaseState.Pending);
        reservation.BannerRows().ShouldHaveSingleItem();

        var restored = rig.SavedHistories[1];
        restored.SentinelLeases().ShouldHaveSingleItem().ShouldBe(failed);
        restored.BannerRows().ShouldBeEmpty();
        var report = restored.StatusRows().ShouldHaveSingleItem().Content;
        report.Mentions("ended with an error").ShouldBeTrue(report);
        report.Mentions("not queued").ShouldBeTrue(report);
    }

    // Proves: a refused startup admission keeps the restart notice that was saved with the reservation, takes the banner back and restores the original lease, all in one further save.
    [Fact]
    public async Task RefusedStartupRecovery_KeepsTheRestartNotice_AndRestoresTheLeaseInOneSave()
    {
        var rig = await Rig.StartAsync(leaseState: RunLeaseState.Running, postAccepted: false);
        var running = rig.Session.FindRunLease(rig.LeaseId.ShouldNotBeNull()).ShouldNotBeNull();

        await rig.Service.StartedAsync(CancellationToken.None);

        rig.Events.ShouldBe(["save", "post", "save"], "reserve, post, then one restore save");
        var restored = rig.SavedHistories[1];
        restored.SentinelLeases().ShouldHaveSingleItem().ShouldBe(running);
        restored.BannerRows().ShouldBeEmpty();
        restored.StatusRows().ShouldHaveSingleItem().Content.ShouldBe(InterruptedTurnNotificationService.AgentOnlyNotificationContent);
        rig.Activities.ShouldHaveSingleItem().Message.ShouldBe(InterruptedTurnNotificationService.AgentOnlyNotificationContent);
    }

    // Proves: when the save that restores a refused reservation does not persist, the live session goes back to exactly what was durably saved with the reservation, and the next boot queues that same reservation once without spending another attempt.
    [Fact]
    public async Task RefusedAdmission_ThenAFailedRestoreSave_LeavesTheReservationForTheNextBoot_WhichQueuesItOnce()
    {
        var rig = await Rig.StartAsync(postAccepted: false);
        var durable = new List<IReadOnlyList<SessionEntry>>();
        var saveCalls = 0;
        rig.Store.Setup(store => store.SaveAsync(
                It.IsAny<GatewaySession>(), It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()))
            .Returns((GatewaySession session, SessionWriteFence _, CancellationToken _) =>
            {
                // Saves: 1 reservation (persists), 2 restore after the refusal (fails), then the next boot's.
                if (++saveCalls == 2)
                    throw new IOException("synthetic persistence failure");

                durable.Add(session.GetHistorySnapshot());
                return Task.FromResult(SessionSaveOutcome.Persisted);
            });

        await rig.PublishTerminalFailureAsync();

        // Memory matches what is durable, so no uncommitted row can later be mistaken for a committed one.
        var afterFailure = rig.Session.GetHistorySnapshot();
        afterFailure.ShouldBe(durable.ShouldHaveSingleItem());
        var reservation = afterFailure.SentinelLeases().ShouldHaveSingleItem();
        reservation.State.ShouldBe(RunLeaseState.Pending);
        reservation.Attempt.ShouldBe(1);
        rig.Activities.ShouldBeEmpty("nothing was queued, so nothing is announced");

        rig.PostAccepted = true;
        await rig.NextBoot().StartedAsync(CancellationToken.None);

        rig.Orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Exactly(2));
        var posted = rig.Orchestrator.Invocations.Last().Arguments.Single().ShouldBeOfType<InboundMessage>();
        var recovery = posted.Recovery.ShouldNotBeNull();
        recovery.SuccessorLeaseId.ShouldBe(reservation.LeaseId, "the same reservation is queued again");
        recovery.Attempt.ShouldBe(1, "queuing a lost reservation again spends nothing");
        var lease = rig.Session.GetHistorySnapshot().SentinelLeases().ShouldHaveSingleItem();
        lease.State.ShouldBe(RunLeaseState.Pending);
        lease.Attempt.ShouldBe(1);
        lease.QueuedBy.ShouldNotBe(reservation.QueuedBy, "the reservation now belongs to the boot that queued it again");
    }

    // Proves: a report is tied to the failed RUN, not to its words: two independent failures that read exactly alike each get their own report, while a repeated signal for either adds nothing.
    [Fact]
    public async Task TwoIndependentFailuresWithIdenticalDiagnostics_EachGetTheirOwnReport_AndARepeatAddsNothing()
    {
        var rig = await Rig.StartAsync(withOrchestrator: false);
        await rig.PublishTerminalFailureAsync();
        await rig.PublishTerminalFailureAsync();
        rig.Session.GetHistorySnapshot().StatusRows().ShouldHaveSingleItem("a repeated signal for the same failure adds no second report");

        var laterInput = new SessionEntry { Role = MessageRole.User, Content = "A later instruction.", Timestamp = DateTimeOffset.UtcNow };
        rig.Session.AddEntry(laterInput);
        var later = new RunLease
        {
            LeaseId = RunLeaseId.Create(),
            State = RunLeaseState.Failed,
            Attempt = 0,
            Input = InputRef.For(laterInput),
            Origin = InputRef.For(laterInput),
            FailedAt = DateTimeOffset.UtcNow,
            FailureDetail = FailureDetail,
            FailedRunId = AgentRunId.From("run-failed-2")
        };
        rig.Session.AddEntry(new SessionEntry
        {
            Role = MessageRole.System,
            Content = "[agent turn in progress - gateway restarted if visible]",
            IsCrashSentinel = true,
            RunLease = later
        });
        var laterFailure = new SessionLifecycleEvent(
            rig.Session.SessionId.Value, rig.Session.AgentId.Value, SessionLifecycleEventType.TerminalFailure, rig.Session, later.LeaseId);

        await rig.Lifecycle.PublishAsync(laterFailure);
        await rig.Lifecycle.PublishAsync(laterFailure);

        var reports = rig.Session.GetHistorySnapshot().StatusRows();
        reports.Count.ShouldBe(2, "one report per failed run, however often its signal repeats");
        reports[1].Content.ShouldBe(reports[0].Content, "the two failures read exactly alike");
        reports[0].AgentRunId.ShouldBe(AgentRunId.From("run-failed-1"));
        reports[1].AgentRunId.ShouldBe(AgentRunId.From("run-failed-2"));
    }

    // Proves: a shutdown that lands after the reservation is durable posts nothing and leaves the reservation for the next boot, which costs no budget.
    [Fact]
    public async Task CancellationAfterTheReservationIsDurable_PostsNothingAndLeavesTheReservation()
    {
        var rig = await Rig.StartAsync(leaseState: RunLeaseState.Running);
        using var cancellation = new CancellationTokenSource();
        rig.Store.Setup(store => store.SaveAsync(
                It.IsAny<GatewaySession>(), It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cancellation.Cancel();
                return Task.FromResult(SessionSaveOutcome.Persisted);
            });

        await Should.ThrowAsync<OperationCanceledException>(() => rig.Service.StartedAsync(cancellation.Token));

        rig.Orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Never, "admission is checked for cancellation before the post");
        var reservation = rig.Session.GetHistorySnapshot().SentinelLeases().ShouldHaveSingleItem();
        reservation.State.ShouldBe(RunLeaseState.Pending);
        reservation.Attempt.ShouldBe(1);
    }

    // Proves: a reservation save that cancellation cuts short is taken back before the cancellation propagates: memory keeps the failed lease and none of the optimistic report, and nothing is posted or announced.
    [Fact]
    public async Task CancellationDuringTheReservationSave_TakesTheReservationBack_AndPostsNothing()
    {
        var rig = await Rig.StartAsync();
        var failedLeaseId = rig.LeaseId.ShouldNotBeNull();
        var failed = rig.Session.FindRunLease(failedLeaseId).ShouldNotBeNull();
        var before = rig.Session.GetHistorySnapshot();
        using var cancellation = new CancellationTokenSource();
        rig.Store.Setup(store => store.SaveAsync(
                It.IsAny<GatewaySession>(), It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });

        // The lifecycle publisher logs a subscriber's cancellation and never rethrows it, so what is observed
        // is the state the cancelled handler leaves behind.
        await rig.PublishTerminalFailureAsync(cancellation.Token);

        rig.Session.GetHistorySnapshot().ShouldBe(before);
        rig.Session.FindRunLease(failedLeaseId).ShouldBe(failed, "the lease is the failed one it was: nothing was reserved");
        rig.Orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Never);
        rig.Activities.ShouldBeEmpty();
    }

    // Proves: a restore save that cancellation cuts short is taken back before the cancellation propagates, so memory returns to the reservation that is already durable instead of holding a restored lease and a refusal report the store never saw.
    [Fact]
    public async Task CancellationDuringTheRestoreSave_ReturnsMemoryToTheDurableReservation()
    {
        var rig = await Rig.StartAsync(postAccepted: false);
        using var cancellation = new CancellationTokenSource();
        var durable = new List<IReadOnlyList<SessionEntry>>();
        var saveCalls = 0;
        rig.Store.Setup(store => store.SaveAsync(
                It.IsAny<GatewaySession>(), It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()))
            .Returns((GatewaySession session, SessionWriteFence _, CancellationToken _) =>
            {
                // Saves: 1 reservation (persists), 2 restore after the refusal (cut short by cancellation).
                if (++saveCalls == 2)
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }

                durable.Add(session.GetHistorySnapshot());
                return Task.FromResult(SessionSaveOutcome.Persisted);
            });

        await rig.PublishTerminalFailureAsync(cancellation.Token);

        saveCalls.ShouldBe(2, "fixture: the reservation persisted and the restore was cut short");
        var afterwards = rig.Session.GetHistorySnapshot();
        afterwards.ShouldBe(durable.ShouldHaveSingleItem());
        afterwards.SentinelLeases().ShouldHaveSingleItem().State.ShouldBe(RunLeaseState.Pending);
        afterwards.StatusRows().ShouldHaveSingleItem().Content.Mentions("not queued")
            .ShouldBeFalse("the refusal report the store never saw must not be left in memory");
        rig.Activities.ShouldBeEmpty("nothing was queued, so nothing is announced");
    }

    // Proves: the same at startup, where the cancellation also propagates to the caller: a restore save cut short returns memory to the reservation that is already durable, restart notice and banner included.
    [Fact]
    public async Task CancellationDuringTheRestoreSave_AtStartup_ReturnsMemoryToTheDurableReservation_AndPropagates()
    {
        var rig = await Rig.StartAsync(leaseState: RunLeaseState.Running, postAccepted: false);
        using var cancellation = new CancellationTokenSource();
        var durable = new List<IReadOnlyList<SessionEntry>>();
        var saveCalls = 0;
        rig.Store.Setup(store => store.SaveAsync(
                It.IsAny<GatewaySession>(), It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()))
            .Returns((GatewaySession session, SessionWriteFence _, CancellationToken _) =>
            {
                if (++saveCalls == 2)
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }

                durable.Add(session.GetHistorySnapshot());
                return Task.FromResult(SessionSaveOutcome.Persisted);
            });

        await Should.ThrowAsync<OperationCanceledException>(() => rig.Service.StartedAsync(cancellation.Token));

        var afterwards = rig.Session.GetHistorySnapshot();
        afterwards.ShouldBe(durable.ShouldHaveSingleItem());
        afterwards.SentinelLeases().ShouldHaveSingleItem().State.ShouldBe(RunLeaseState.Pending);
        afterwards.BannerRows().ShouldHaveSingleItem();
        afterwards.StatusRows().ShouldHaveSingleItem().Content.ShouldBe(InterruptedTurnNotificationService.AgentOnlyNotificationContent);
    }

    // Proves: a report-and-consume save that cancellation cuts short is taken back too: the lease that was to be consumed is still on the session and the report that was to be written is not.
    [Fact]
    public async Task CancellationDuringABlockedReportSave_PutsTheLeaseBackAndTheReportAway()
    {
        var rig = await Rig.StartAsync(withInitiatingInput: false);
        var before = rig.Session.GetHistorySnapshot();
        using var cancellation = new CancellationTokenSource();
        rig.Store.Setup(store => store.SaveAsync(
                It.IsAny<GatewaySession>(), It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });

        await rig.PublishTerminalFailureAsync(cancellation.Token);

        rig.Session.GetHistorySnapshot().ShouldBe(before);
        rig.Session.GetHistorySnapshot().StatusRows().ShouldBeEmpty();
        rig.Activities.ShouldBeEmpty();
    }

    // Proves: a startup scan finds a lease that was still running as a restart interruption and says so, with the existing restart texts, before the banner, and reserves the replacement on the next attempt.
    [Fact]
    public async Task StartupScan_ARunningLease_IsARestartInterruption()
    {
        var rig = await Rig.StartAsync(leaseState: RunLeaseState.Running);

        await rig.Service.StartedAsync(CancellationToken.None);

        var history = rig.Session.GetHistorySnapshot();
        var notice = history.StatusRows().ShouldHaveSingleItem().Content;
        notice.ShouldBe(InterruptedTurnNotificationService.AgentOnlyNotificationContent);
        history.BannerRows().ShouldHaveSingleItem().Content.ShouldStartWith("[PLATFORM RESTART - AUTOMATIC REPLAY 1 of 2]");
        IndexWhere(history, entry => entry.Role.Equals(MessageRole.Notification))
            .ShouldBeLessThan(IndexWhere(history, entry => entry.IsReplayBanner), "the startup notice precedes the banner");
        var posted = rig.Orchestrator.Invocations.Single().Arguments.Single().ShouldBeOfType<InboundMessage>();
        posted.Recovery.ShouldNotBeNull().Attempt.ShouldBe(1);
        history.SentinelLeases().ShouldHaveSingleItem().State.ShouldBe(RunLeaseState.Pending);
    }

    // Proves: a startup scan that finds a failed lease nobody recovered reports an error-ended run, not a restart, and still queues the replacement.
    [Fact]
    public async Task StartupScan_AFailedLease_IsReportedAsAFailureNotARestart()
    {
        var rig = await Rig.StartAsync();

        await rig.Service.StartedAsync(CancellationToken.None);

        var history = rig.Session.GetHistorySnapshot();
        var notice = history.StatusRows().ShouldHaveSingleItem().Content;
        notice.Mentions("ended with an error").ShouldBeTrue(notice);
        notice.Mentions("queued").ShouldBeTrue(notice);
        AssertNoRestartClaims(notice);
        history.BannerRows().ShouldHaveSingleItem().Content.ShouldStartWith("[AUTOMATIC RECOVERY 1 of 2]");
        rig.Orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Once);
    }

    // Proves: a pending reservation whose queuing instance is gone is queued again at the SAME attempt, naming the same reservation, and no budget is spent.
    [Fact]
    public async Task StartupScan_APendingLeaseFromAnotherInstance_IsQueuedAgainWithoutSpendingBudget()
    {
        var rig = await Rig.StartAsync(leaseState: RunLeaseState.Pending, leaseAttempt: 1);
        var pendingId = rig.LeaseId.ShouldNotBeNull();

        await rig.Service.StartedAsync(CancellationToken.None);

        var posted = rig.Orchestrator.Invocations.Single().Arguments.Single().ShouldBeOfType<InboundMessage>();
        var recovery = posted.Recovery.ShouldNotBeNull();
        recovery.SuccessorLeaseId.ShouldBe(pendingId, "the same reservation is queued again");
        recovery.Attempt.ShouldBe(1, "re-queuing a lost replacement spends nothing");
        var lease = rig.Session.FindRunLease(pendingId).ShouldNotBeNull();
        lease.State.ShouldBe(RunLeaseState.Pending);
        lease.Attempt.ShouldBe(1);
        lease.QueuedBy.ShouldNotBe("another-instance", "the reservation now belongs to this instance");
    }

    // Proves: a startup scan that runs while this instance's own replacement is waiting its turn leaves it alone: one replacement between the live handler and the scan.
    [Fact]
    public async Task StartupScan_AfterTheLiveHandlerQueuedAReplacement_QueuesNoSecondOne()
    {
        var rig = await Rig.StartAsync();
        await rig.PublishTerminalFailureAsync();
        var rowsAfterLive = rig.Session.GetHistorySnapshot().Count;

        await rig.Service.StartedAsync(CancellationToken.None);

        rig.Orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Once);
        rig.Session.GetHistorySnapshot().Count.ShouldBe(rowsAfterLive);
    }

    // Proves: a startup scan leaves a session with a live turn alone: that turn's marker is not an interrupted turn.
    [Fact]
    public async Task StartupScan_WhileATurnIsLive_LeavesTheSessionAlone()
    {
        var tracker = new SessionTurnTracker();
        using var liveTurn = tracker.BeginTurn("sess-run-failure");
        var rig = await Rig.StartAsync(leaseState: RunLeaseState.Running, turnTracker: tracker);
        var before = rig.Session.GetHistorySnapshot();

        await rig.Service.StartedAsync(CancellationToken.None);

        rig.Session.GetHistorySnapshot().ShouldBe(before);
        rig.Orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Never);
        rig.Activities.ShouldBeEmpty();
    }

    // A live run failure never restarted anything, so neither claim is true of it.
    private static void AssertNoRestartClaims(string text)
    {
        text.Mentions("restart").ShouldBeFalse($"a live run failure must not claim a restart: {text}");
        text.Mentions("no human action is required").ShouldBeFalse($"a live run failure must not say no action is needed: {text}");
    }

    // The failure detail is what sits between the notice's fixed lead and the sentence that reports what admission did.
    private static string DetailOf(string notice)
    {
        notice.ShouldStartWith(NoticeLead);
        var end = notice.IndexOf(" Automatic recovery", StringComparison.Ordinal);
        end.ShouldBeGreaterThan(NoticeLead.Length, notice);
        return notice[NoticeLead.Length..end];
    }

    private static int IndexWhere(IReadOnlyList<SessionEntry> history, Func<SessionEntry, bool> predicate)
    {
        for (var index = 0; index < history.Count; index++)
        {
            if (predicate(history[index]))
                return index;
        }

        throw new InvalidOperationException("No history row matched.");
    }

    private sealed class Rig
    {
        private Rig()
        {
        }

        public required GatewaySession Session { get; init; }

        public required Mock<ISessionStore> Store { get; init; }

        public required Mock<IInboundMessageOrchestrator> Orchestrator { get; init; }

        public required List<GatewayActivity> Activities { get; init; }

        public required SessionLifecycleEvents Lifecycle { get; init; }

        public required InterruptedTurnNotificationService Service { get; init; }

        // What the default store and orchestrator did, in call order: "save" and "post".
        public required List<string> Events { get; init; }

        // The transcript each default save carried, taken when the save was called.
        public required List<IReadOnlyList<SessionEntry>> SavedHistories { get; init; }

        // Another service over the same store, orchestrator and session, as a later boot of the process would
        // be. It is not subscribed to lifecycle events, because the boot before it is gone.
        public required Func<InterruptedTurnNotificationService> NextBoot { get; init; }

        public required StrongBox<bool> PostResult { get; init; }

        // Whether the default orchestrator accepts what is posted to it; can be changed between steps.
        public bool PostAccepted
        {
            get => PostResult.Value;
            set => PostResult.Value = value;
        }

        // The lease the fixture's sentinel carries and the event names; null for a sentinel without one.
        public required RunLeaseId? LeaseId { get; init; }

        public Task PublishTerminalFailureAsync(CancellationToken cancellationToken = default)
            => Lifecycle.PublishAsync(
                new SessionLifecycleEvent(
                    Session.SessionId.Value, Session.AgentId.Value, SessionLifecycleEventType.TerminalFailure, Session, LeaseId),
                cancellationToken);

        public static async Task<Rig> StartAsync(
            bool withInitiatingInput = true,
            bool postAccepted = true,
            bool withOrchestrator = true,
            bool hasHuman = false,
            ChannelKey? channelType = null,
            int leaseAttempt = 0,
            int maxAttempts = 2,
            string? failureDetail = FailureDetail,
            RunLeaseState? leaseState = RunLeaseState.Failed,
            string? queuedBy = null,
            ISessionTurnTracker? turnTracker = null,
            bool inputIsAReplacement = false)
        {
            var session = new GatewaySession
            {
                SessionId = SessionId.From("sess-run-failure"),
                ConversationId = ConversationId.From("conv-run-failure"),
                AgentId = AgentId.From("agent-run-failure"),
                Status = SessionStatus.Active,
                UpdatedAt = DateTimeOffset.UtcNow,
                SessionType = SessionType.UserAgent,
                ChannelType = channelType ?? ChannelKey.From("api")
            };

            // The input the run was admitted for. An agent's kickoff is an assistant-voiced row with no sender
            // label, which is exactly what the lease exists to name; a lease-less sentinel can only name a user row.
            var origin = new SessionEntry
            {
                Role = leaseState is null ? MessageRole.User : MessageRole.Assistant,
                Content = Instruction,
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            session.AddEntry(origin);

            // A run on a replacement's attempt was started for the replacement's own stored copy of the input,
            // while the lease still names the original as the origin of the whole chain.
            SessionEntry? replacementRow = null;
            if (inputIsAReplacement)
            {
                replacementRow = origin with { Timestamp = origin.Timestamp.AddSeconds(30) };
                session.AddEntry(replacementRow);
            }

            RunLease? lease = null;
            if (leaseState is { } state)
            {
                // A lease that names a message the transcript does not hold names no input at all.
                var named = withInitiatingInput
                    ? origin
                    : new SessionEntry { Role = MessageRole.Assistant, Content = Instruction, Timestamp = origin.Timestamp.AddHours(-1) };
                lease = new RunLease
                {
                    LeaseId = RunLeaseId.Create(),
                    State = state,
                    Attempt = leaseAttempt,
                    Input = InputRef.For(replacementRow ?? named),
                    Origin = InputRef.For(named),
                    FailedAt = state == RunLeaseState.Failed ? DateTimeOffset.UtcNow : null,
                    FailureDetail = state == RunLeaseState.Failed ? failureDetail : null,
                    FailedRunId = state == RunLeaseState.Failed ? AgentRunId.From("run-failed-1") : null,
                    QueuedBy = state == RunLeaseState.Pending ? queuedBy ?? "another-instance" : null
                };
            }

            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.System,
                Content = "[agent turn in progress - gateway restarted if visible]",
                IsCrashSentinel = true,
                RunLease = lease
            });
            if (leaseState is null && leaseAttempt > 0)
                session.Metadata[InterruptedTurnNotificationService.MetadataKeyReplayCount] = leaseAttempt;

            var events = new List<string>();
            var savedHistories = new List<IReadOnlyList<SessionEntry>>();
            var postResult = new StrongBox<bool>(postAccepted);

            var store = new Mock<ISessionStore>();
            store.Setup(s => s.ListUnresolvedCrashSentinelsAsync(
                    It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UnresolvedCrashSentinelPage(
                    [new UnresolvedCrashSentinelRow(session.SessionId, session.AgentId)], null));
            store.Setup(s => s.GetAsync(session.SessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
            store.Setup(s => s.SaveAsync(
                    It.IsAny<GatewaySession>(), It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()))
                .Returns((GatewaySession saved, SessionWriteFence _, CancellationToken _) =>
                {
                    events.Add("save");
                    savedHistories.Add(saved.GetHistorySnapshot());
                    return Task.FromResult(SessionSaveOutcome.Persisted);
                });

            var orchestrator = new Mock<IInboundMessageOrchestrator>();
            orchestrator.Setup(o => o.Post(It.IsAny<InboundMessage>())).Returns(() =>
            {
                events.Add("post");
                return postResult.Value;
            });

            var activities = new List<GatewayActivity>();
            var broadcaster = new Mock<IActivityBroadcaster>();
            broadcaster.Setup(b => b.PublishAsync(It.IsAny<GatewayActivity>(), It.IsAny<CancellationToken>()))
                .Returns((GatewayActivity activity, CancellationToken _) =>
                {
                    activities.Add(activity);
                    return ValueTask.CompletedTask;
                });

            var participant = hasHuman
                ? new SessionParticipant { CitizenId = CitizenId.Of(UserId.From("user-a")) }
                : new SessionParticipant { CitizenId = CitizenId.Of(session.AgentId) };
            var conversations = new Mock<IConversationStore>();
            conversations.Setup(c => c.GetAsync(session.ConversationId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Conversation
                {
                    ConversationId = session.ConversationId,
                    AgentId = session.AgentId,
                    Participants = [participant]
                });

            var registry = new Mock<IAgentRegistry>();
            registry.Setup(r => r.GetAll()).Returns(
            [
                new AgentDescriptor
                {
                    AgentId = session.AgentId,
                    DisplayName = session.AgentId.Value,
                    ModelId = "gpt-4.1",
                    ApiProvider = "copilot"
                }
            ]);

            var lifecycle = new SessionLifecycleEvents(NullLogger<SessionLifecycleEvents>.Instance);
            InterruptedTurnNotificationService BuildService() => new(
                store.Object,
                registry.Object,
                broadcaster.Object,
                Mock.Of<IConversationEventPublisher>(),
                NullLogger<InterruptedTurnNotificationService>.Instance,
                withOrchestrator ? orchestrator.Object : null,
                Options.Create(new GatewayOptions { AutoReplayInterruptedTurns = false, MaxAutoReplayAttempts = maxAttempts }),
                conversations.Object,
                lifecycle,
                turnTracker: turnTracker);
            var service = BuildService();
            await service.StartAsync(CancellationToken.None);

            return new Rig
            {
                Session = session,
                Store = store,
                Orchestrator = orchestrator,
                Activities = activities,
                Lifecycle = lifecycle,
                Service = service,
                Events = events,
                SavedHistories = savedHistories,
                NextBoot = BuildService,
                PostResult = postResult,
                LeaseId = lease?.LeaseId
            };
        }
    }
}
