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

    // Proves: a queued replacement is reported as queued after the failure, with the banner after the notice, and consumes the sentinel and one attempt.
    [Fact]
    public async Task QueuedRecovery_ReportsTheFailureThenTheQueuedAttempt_AndConsumesTheSentinel()
    {
        var rig = await Rig.StartAsync();

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

        rig.Orchestrator.Verify(
            o => o.Post(It.Is<InboundMessage>(m => m.Content == Instruction && m.SpeakAs == MessageRole.User)), Times.Once);
        history.HasCrashSentinel().ShouldBeFalse("a queued replacement consumes the sentinel");
        rig.Session.Metadata[InterruptedTurnNotificationService.MetadataKeyReplayCount].ShouldBe(1);
        rig.Activities.ShouldHaveSingleItem().Message.ShouldBe(notice, "the activity mirrors the persisted notice");
    }

    // Proves: with no message to replay the failure is reported with a not-queued blocker and a next step, nothing is admitted, and sentinel retention is exactly what it was before this reporting existed.
    [Fact]
    public async Task NoInitiatingInput_ReportsANotQueuedBlocker_AndLeavesTheSentinelAsItWas()
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
        history.HasCrashSentinel().ShouldBeTrue("only an accepted replay consumes the sentinel, as before");
        rig.Orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Never);
        rig.Session.Metadata.ShouldNotContainKey(InterruptedTurnNotificationService.MetadataKeyReplayCount);
        rig.Activities.ShouldHaveSingleItem().Message.ShouldBe(notice);
    }

    // Proves: a spent attempt budget is reported with a not-queued blocker and a next step, the budget is untouched, and sentinel retention is exactly what it was before this reporting existed.
    [Fact]
    public async Task SpentBudget_ReportsANotQueuedBlocker_AndLeavesTheSentinelAsItWas()
    {
        var rig = await Rig.StartAsync(existingReplayCount: 2, maxAttempts: 2);

        await rig.PublishTerminalFailureAsync();

        var history = rig.Session.GetHistorySnapshot();
        var notice = history.StatusRows().ShouldHaveSingleItem().Content;
        notice.Mentions("error").ShouldBeTrue(notice);
        notice.Mentions("not queued").ShouldBeTrue(notice);
        notice.Mentions("post a message").ShouldBeTrue(notice);
        AssertNoRestartClaims(notice);
        history.BannerRows().ShouldBeEmpty();
        history.HasCrashSentinel().ShouldBeTrue("only an accepted replay consumes the sentinel, as before");
        rig.Orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Never);
        rig.Session.Metadata[InterruptedTurnNotificationService.MetadataKeyReplayCount].ShouldBe(2);
    }

    // Proves: a refused admission is reported as not queued and keeps the sentinel and the budget, and a later accepted attempt is reported as queued, with its banner, one attempt and the sentinel consumed.
    [Fact]
    public async Task RefusedAdmission_ReportsNotQueued_KeepsTheSentinelAndBudget_AndALaterAcceptedAttemptIsReportedQueued()
    {
        var rig = await Rig.StartAsync(postAccepted: false);

        await rig.PublishTerminalFailureAsync();

        var refused = rig.Session.GetHistorySnapshot().StatusRows().ShouldHaveSingleItem().Content;
        refused.Mentions("error").ShouldBeTrue(refused);
        refused.Mentions("not queued").ShouldBeTrue(refused);
        AssertNoRestartClaims(refused);
        rig.Session.History.ShouldContain(entry => entry.IsCrashSentinel);
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
        history.HasCrashSentinel().ShouldBeFalse("an accepted replay consumes the sentinel");
        rig.Session.Metadata[InterruptedTurnNotificationService.MetadataKeyReplayCount].ShouldBe(1);
        rig.Orchestrator.Verify(o => o.Post(It.IsAny<InboundMessage>()), Times.Exactly(2));
        rig.Activities.Count.ShouldBe(2);
        rig.Activities[0].Message.ShouldBe(refused);
        rig.Activities[1].Message.ShouldBe(rows[1].Content);
    }

    // Proves: with no orchestrator the failure is reported with a not-queued reason and a next step, and the sentinel is consumed as it is for any session replay does not apply to.
    [Fact]
    public async Task NoOrchestrator_ReportsRecoveryIsNotAvailable_AndConsumesTheSentinelAsBefore()
    {
        var rig = await Rig.StartAsync(withOrchestrator: false);

        await rig.PublishTerminalFailureAsync();

        var history = rig.Session.GetHistorySnapshot();
        var notice = history.StatusRows().ShouldHaveSingleItem().Content;
        notice.Mentions("not queued").ShouldBeTrue(notice);
        notice.Mentions("not available").ShouldBeTrue(notice);
        notice.Mentions("post a message").ShouldBeTrue(notice);
        AssertNoRestartClaims(notice);
        history.HasCrashSentinel().ShouldBeFalse();
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

    // Proves: a failed final save announces nothing, so no activity claims a notice the store does not hold.
    [Fact]
    public async Task PersistenceFailure_AnnouncesNothing()
    {
        var rig = await Rig.StartAsync();
        rig.Store.Setup(store => store.SaveAsync(
                It.IsAny<GatewaySession>(), It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("synthetic persistence failure"));

        await rig.PublishTerminalFailureAsync();

        rig.Store.Verify(
            store => store.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()),
            Times.Once);
        rig.Activities.ShouldBeEmpty();
    }

    // Proves: the startup scan keeps its restart wording, its notice-before-banner history order and its banner header, because a restart notice is true whatever admission decides.
    [Fact]
    public async Task StartupScan_KeepsTheRestartTextsAndTheNoticeBeforeBannerOrder()
    {
        var rig = await Rig.StartAsync(withFailedCompletion: false);

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

        public Task PublishTerminalFailureAsync()
            => Lifecycle.PublishAsync(new SessionLifecycleEvent(
                Session.SessionId.Value, Session.AgentId.Value, SessionLifecycleEventType.TerminalFailure, Session));

        public static async Task<Rig> StartAsync(
            bool withInitiatingInput = true,
            bool postAccepted = true,
            bool withOrchestrator = true,
            bool hasHuman = false,
            ChannelKey? channelType = null,
            int existingReplayCount = 0,
            int maxAttempts = 2,
            string? failureDetail = FailureDetail,
            bool withFailedCompletion = true)
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

            // A kickoff row has no durable sender provenance, so it names no input to replay; a user-voiced row does.
            session.AddEntry(new SessionEntry
            {
                Role = withInitiatingInput ? MessageRole.User : MessageRole.Assistant,
                Content = Instruction,
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
            session.AddEntry(new SessionEntry
            {
                Role = MessageRole.System,
                Content = "[agent turn in progress - gateway restarted if visible]",
                IsCrashSentinel = true
            });
            if (existingReplayCount > 0)
                session.Metadata[InterruptedTurnNotificationService.MetadataKeyReplayCount] = existingReplayCount;
            if (withFailedCompletion)
                session.RunCompletion = new RunCompletionSignal("Failed", [], "Error", failureDetail, null, null, null, 0);

            var store = new Mock<ISessionStore>();
            store.Setup(s => s.ListUnresolvedCrashSentinelsAsync(
                    It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UnresolvedCrashSentinelPage(
                    [new UnresolvedCrashSentinelRow(session.SessionId, session.AgentId)], null));
            store.Setup(s => s.GetAsync(session.SessionId, It.IsAny<CancellationToken>())).ReturnsAsync(session);
            store.Setup(s => s.SaveAsync(
                    It.IsAny<GatewaySession>(), It.IsAny<SessionWriteFence>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(SessionSaveOutcome.Persisted);

            var orchestrator = new Mock<IInboundMessageOrchestrator>();
            orchestrator.Setup(o => o.Post(It.IsAny<InboundMessage>())).Returns(postAccepted);

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
            var service = new InterruptedTurnNotificationService(
                store.Object,
                registry.Object,
                broadcaster.Object,
                Mock.Of<IConversationEventPublisher>(),
                NullLogger<InterruptedTurnNotificationService>.Instance,
                withOrchestrator ? orchestrator.Object : null,
                Options.Create(new GatewayOptions { AutoReplayInterruptedTurns = false, MaxAutoReplayAttempts = maxAttempts }),
                conversations.Object,
                lifecycle);
            await service.StartAsync(CancellationToken.None);

            return new Rig
            {
                Session = session,
                Store = store,
                Orchestrator = orchestrator,
                Activities = activities,
                Lifecycle = lifecycle,
                Service = service
            };
        }
    }
}
