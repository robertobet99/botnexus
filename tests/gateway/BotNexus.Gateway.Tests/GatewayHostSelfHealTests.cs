using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for the write-time self-heal of orphaned crash sentinels (#2030). When an inbound
/// message arrives for a session that carries a crash sentinel but has no live in-memory turn,
/// the sentinel is an orphan (a turn that died mid-flight) and is cleared at ingress so the
/// session unblocks the instant the user retries - no gateway restart. When a turn IS live the
/// sentinel is legitimate and must be left in place so the message keeps queuing behind it.
/// </summary>
public sealed partial class GatewayHostTests
{
    /// <summary>
    /// A tracker whose live-turn answer is fixed, so tests can model "a turn is already
    /// executing for this session" vs. "no live turn" deterministically.
    /// </summary>
    private sealed class StubTurnTracker(bool hasLiveTurn) : ISessionTurnTracker
    {
        public IDisposable BeginTurn(string sessionId) => new NoopScope();

        public bool HasLiveTurn(string sessionId) => hasLiveTurn;

        private sealed class NoopScope : IDisposable
        {
            public void Dispose() { }
        }
    }

    [Fact]
    public async Task DispatchAsync_SentinelWithNoLiveTurn_SelfHealsAtIngressAndProceeds()
    {
        // Arrange: a session that already carries an orphaned crash sentinel and NO live turn.
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-heal"]);
        var handle = CreatePromptHandle("agent-heal", "session-heal", "healed-response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
            AgentId.From("agent-heal"), SessionId.From("session-heal"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = SessionId.From("session-heal"),
            AgentId = AgentId.From("agent-heal")
        };
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.System,
            Content = "[agent turn in progress - gateway restarted if visible]",
            IsCrashSentinel = true
        });

        // Record whether a sentinel was still present at the FIRST SaveAsync - the write-ahead
        // user-message save that runs AFTER ingress self-heal but BEFORE PrepareTurnAsync writes
        // its own fresh sentinel. If self-heal fired, this observes zero sentinels.
        bool? sentinelPresentAtFirstSave = null;
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
            SessionId.From("session-heal"), AgentId.From("agent-heal"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => sentinelPresentAtFirstSave ??= session.History.Any(e => e.IsCrashSentinel));

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(),
            CreateChannelManager(channel.Object), turnTracker: new StubTurnTracker(hasLiveTurn: false));

        // Act
        await host.DispatchAsync(CreateMessage("retry please", sessionId: "session-heal"));

        // Assert: the orphaned sentinel was cleared at ingress (before the write-ahead save)...
        sentinelPresentAtFirstSave.ShouldBe(false,
            "orphaned sentinel must be self-healed at message ingress when no turn is live (#2030)");
        // ...and the message proceeded to completion with a delivered reply.
        channel.Verify(c => c.SendAsync(
            It.Is<OutboundMessage>(m => m.Content == "healed-response"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_SentinelWithLiveTurn_DoesNotSelfHeal()
    {
        // Arrange: a session with a sentinel AND a live turn already executing. The sentinel is
        // legitimate (it belongs to the in-flight turn) and must NOT be cleared at ingress.
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-live"]);
        var handle = CreatePromptHandle("agent-live", "session-live", "response");
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(
            AgentId.From("agent-live"), SessionId.From("session-live"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);

        var session = new GatewaySession
        {
            SessionId = SessionId.From("session-live"),
            AgentId = AgentId.From("agent-live")
        };
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.System,
            Content = "[agent turn in progress - gateway restarted if visible]",
            IsCrashSentinel = true
        });

        bool? sentinelPresentAtFirstSave = null;
        var sessions = new Mock<ISessionStore>();
        sessions.Setup(s => s.GetOrCreateAsync(
            SessionId.From("session-live"), AgentId.From("agent-live"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        sessions.Setup(s => s.SaveAsync(session, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => sentinelPresentAtFirstSave ??= session.History.Any(e => e.IsCrashSentinel));

        var channel = CreateChannelAdapter("web", supportsStreaming: false);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions.Object, new RecordingActivityBroadcaster(),
            CreateChannelManager(channel.Object), turnTracker: new StubTurnTracker(hasLiveTurn: true));

        // Act
        await host.DispatchAsync(CreateMessage("concurrent message", sessionId: "session-live"));

        // Assert: the sentinel was NOT cleared at ingress because a turn is live - it survives
        // to the write-ahead save, so the message legitimately queues behind the in-flight turn.
        sentinelPresentAtFirstSave.ShouldBe(true,
            "sentinel must be preserved at ingress when a turn is live so the message keeps queuing (#2030)");
    }

    // Proves: the ingress self-heal clears an orphaned sentinel but leaves a pending reservation, and so does the run's own clean completion, because the reservation belongs to a recovery replacement that is queued behind this message and has not started.
    [Theory]
    [InlineData("web", true)]
    [InlineData("web", false)]
    [InlineData("api", false)]
    public async Task DispatchAsync_PendingReservationWithNoLiveTurn_IsNotSelfHealedAndSurvivesTheRunsCompletion(string channelType, bool streams)
    {
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-reserved"]);
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("session-reserved"), AgentId.From("agent-reserved"));
        var origin = new SessionEntry { Role = MessageRole.Assistant, Content = "Apply the scheduled change.", Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1) };
        session.AddEntry(origin);
        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "[orphaned sentinel]", IsCrashSentinel = true });
        var reservation = RunLease.Admit(InputRef.For(origin)).QueueSuccessor("instance-a");
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.System,
            Content = "[agent turn in progress]",
            IsCrashSentinel = true,
            RunLease = reservation
        });
        IReadOnlyList<SessionEntry>? historyWhenTheModelIsCalled = null;
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-reserved"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-reserved"));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                historyWhenTheModelIsCalled = session.GetHistorySnapshot();
                return Task.FromResult(new AgentResponse { Content = "ok" });
            });
        handle.Setup(h => h.StreamAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                historyWhenTheModelIsCalled = session.GetHistorySnapshot();
                return ToAsyncEnumerable([new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "ok" }]);
            });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(AgentId.From("agent-reserved"), SessionId.From("session-reserved"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        var adapter = channelType == "api" ? null : CreateChannelAdapter(channelType, supportsStreaming: streams).Object;
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions, new RecordingActivityBroadcaster(),
            CreateChannelManager(adapter), turnTracker: new StubTurnTracker(hasLiveTurn: false));

        await host.DispatchAsync(CreateMessage("a follow-up", sessionId: "session-reserved", channelType: channelType));

        var leasesDuringTheRun = historyWhenTheModelIsCalled.ShouldNotBeNull()
            .Where(entry => entry.IsCrashSentinel)
            .Select(entry => entry.RunLease)
            .ToList();
        leasesDuringTheRun.Count.ShouldBe(2, "the orphan is cleared; the reservation and the follow-up's own lease remain");
        leasesDuringTheRun.ShouldContain(
            lease => lease != null && lease.LeaseId == reservation.LeaseId && lease.State == RunLeaseState.Pending,
            "the reservation is not an orphan");
        leasesDuringTheRun.ShouldContain(
            lease => lease != null && lease.State == RunLeaseState.Running,
            "the follow-up runs under a lease of its own");
        session.GetHistorySnapshot().Where(entry => entry.IsCrashSentinel).ShouldHaveSingleItem().RunLease.ShouldBe(
            reservation, "the run's clean completion removes its own marker and leaves the reservation for its replacement");
    }

    // Proves: a recovery replacement adopts its own reservation and leaves another pending reservation alone, so every queued replacement still finds the one reserved for it.
    [Fact]
    public async Task DispatchAsync_RecoveryReplacement_LeavesAnotherPendingReservationAlone()
    {
        const string input = "Apply the scheduled change.";
        var router = new Mock<IMessageRouter>();
        router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["agent-reserved"]);
        var sessions = new InMemorySessionStore();
        var session = await sessions.GetOrCreateAsync(SessionId.From("session-reserved"), AgentId.From("agent-reserved"));
        var origin = new SessionEntry { Role = MessageRole.Assistant, Content = input, Timestamp = DateTimeOffset.UtcNow.AddMinutes(-2) };
        var otherOrigin = new SessionEntry { Role = MessageRole.Assistant, Content = "Send the report.", Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1) };
        session.AddEntry(origin);
        session.AddEntry(otherOrigin);
        var own = RunLease.Admit(InputRef.For(origin)).QueueSuccessor("instance-a");
        var other = RunLease.Admit(InputRef.For(otherOrigin)).QueueSuccessor("instance-a");
        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "[agent turn in progress]", IsCrashSentinel = true, RunLease = own });
        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "[agent turn in progress]", IsCrashSentinel = true, RunLease = other });
        IReadOnlyList<SessionEntry>? historyWhenTheModelIsCalled = null;
        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-reserved"));
        handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-reserved"));
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                historyWhenTheModelIsCalled = session.GetHistorySnapshot();
                return Task.FromResult(new AgentResponse { Content = "ok" });
            });
        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(AgentId.From("agent-reserved"), SessionId.From("session-reserved"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        await using var host = CreateHost(
            supervisor.Object, router.Object, sessions, new RecordingActivityBroadcaster(),
            CreateChannelManager(), turnTracker: new StubTurnTracker(hasLiveTurn: false));

        await host.DispatchAsync(CreateMessage(input, sessionId: "session-reserved", channelType: "api")
            with
            {
                SpeakAs = MessageRole.Assistant,
                Recovery = new RecoveryContext { SuccessorLeaseId = own.LeaseId, Attempt = 1, MaxAttempts = 2 }
            });

        var leasesDuringTheRun = historyWhenTheModelIsCalled.ShouldNotBeNull()
            .Where(entry => entry.IsCrashSentinel)
            .Select(entry => entry.RunLease)
            .ToList();
        leasesDuringTheRun.ShouldContain(
            lease => lease != null && lease.LeaseId == own.LeaseId && lease.State == RunLeaseState.Running,
            "the replacement runs under the reservation made for it");
        leasesDuringTheRun.ShouldContain(
            lease => lease != null && lease.LeaseId == other.LeaseId && lease.State == RunLeaseState.Pending,
            "another queued replacement's reservation is not an orphan");
        session.GetHistorySnapshot().Where(entry => entry.IsCrashSentinel).ShouldHaveSingleItem().RunLease.ShouldBe(
            other, "only the other reservation remains once the replacement has finished");
    }
}
