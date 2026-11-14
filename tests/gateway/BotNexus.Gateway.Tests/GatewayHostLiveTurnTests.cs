using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Services;
using BotNexus.Gateway.Sessions;
using BotNexus.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgentUserMessage = BotNexus.Gateway.Abstractions.Models.AgentUserMessage;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// A second inbound message for a session whose turn is live and mid-tool must leave that turn
/// running. <see cref="GatewayHost.ProcessAsync"/> runs abandoned-turn detection (#790) in
/// <c>PrepareTurnAsync</c> before the busy-agent check, so a tool-start row with no tool-end row
/// that is stored between two <c>User</c> rows can be read as a stalled turn even though the turn
/// is healthy. Each test uses a real SQLite store so that every <c>ProcessAsync</c> call loads its
/// own copy of the session, the way two queue keys do in production.
/// </summary>
public sealed class GatewayHostLiveTurnTests
{
    private const string AbandonedNotificationText = "Previous turn did not complete";

    [Fact]
    public async Task ProcessAsync_SecondUserMessageWhileFirstTurnMidTool_DoesNotStopLiveTurnOrNotify()
    {
        await using var rig = new LiveTurnRig();
        var firstTurn = rig.Host.ProcessAsync(CreateMessage("first message", CitizenId.Of(UserId.From("sender-1"))), CancellationToken.None);
        await rig.WaitForToolStartRowAsync();

        await rig.Host.ProcessAsync(CreateMessage("second message", CitizenId.Of(UserId.From("sender-1"))), CancellationToken.None);

        var history = await rig.ReadStoredHistoryAsync();
        rig.Supervisor.Verify(s => s.StopAsync(AgentId.From("agent-a"), SessionId.From("session-1"), It.IsAny<CancellationToken>()), Times.Never,
            "a second message must not stop the handle of a turn that is still running");
        history.ShouldNotContain(
            e => e.Role.Equals(MessageRole.Notification) && e.Content != null && e.Content.Contains(AbandonedNotificationText),
            "a live turn waiting on a tool result is not an abandoned turn");

        rig.Release();
        await firstTurn.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ProcessAsync_SecondAgentMessageWhileFirstTurnMidTool_DoesNotStopLiveTurnOrNotify()
    {
        // Guard: an agent sender is stored as an Assistant row, so only one User row exists and the
        // detector has no previous turn to inspect. This pins the role condition of the test above.
        await using var rig = new LiveTurnRig();
        var firstTurn = rig.Host.ProcessAsync(CreateMessage("first message", CitizenId.Of(UserId.From("sender-1"))), CancellationToken.None);
        await rig.WaitForToolStartRowAsync();

        await rig.Host.ProcessAsync(CreateMessage("second message", CitizenId.Of(AgentId.From("agent-b"))), CancellationToken.None);

        var history = await rig.ReadStoredHistoryAsync();
        history.ShouldContain(e => e.Role.Equals(MessageRole.Assistant) && e.Content == "second message");
        rig.Supervisor.Verify(s => s.StopAsync(AgentId.From("agent-a"), SessionId.From("session-1"), It.IsAny<CancellationToken>()), Times.Never);
        history.ShouldNotContain(
            e => e.Role.Equals(MessageRole.Notification) && e.Content != null && e.Content.Contains(AbandonedNotificationText));

        rig.Release();
        await firstTurn.WaitAsync(TimeSpan.FromSeconds(30));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A host over a real SQLite store and a streaming handle that emits a tool start and then waits
    /// on a gate, so the real streaming helper stores the tool-start row while the turn is live.
    /// </summary>
    private sealed class LiveTurnRig : IAsyncDisposable
    {
        private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"botnexus-live-turn-{Guid.NewGuid():N}.db");
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SqliteSessionStore _store;
        private volatile bool _turnRunning;

        public LiveTurnRig()
        {
            var handle = new Mock<IAgentHandle>();
            handle.SetupGet(h => h.AgentId).Returns(AgentId.From("agent-a"));
            handle.SetupGet(h => h.SessionId).Returns(SessionId.From("session-1"));
            handle.SetupGet(h => h.IsRunning).Returns(() => _turnRunning);
            handle.Setup(h => h.TryFollowUpWhileRunningAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => _turnRunning);
            handle.Setup(h => h.StreamAsync(It.IsAny<AgentUserMessage>(), It.IsAny<CancellationToken>()))
                .Returns((AgentUserMessage _, CancellationToken ct) => GatedToolStream(ct));

            Supervisor = new Mock<IAgentSupervisor>();
            Supervisor.Setup(s => s.GetOrCreateAsync(AgentId.From("agent-a"), SessionId.From("session-1"), It.IsAny<CancellationToken>()))
                .ReturnsAsync(handle.Object);
            Supervisor.Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var router = new Mock<IMessageRouter>();
            router.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>())).ReturnsAsync(["agent-a"]);

            var conversations = new InMemoryConversationStore();
            _store = new SqliteSessionStore(ConnectionString, NullLogger<SqliteSessionStore>.Instance, conversations);
            Host = new GatewayHost(
                Supervisor.Object,
                router.Object,
                _store,
                new NullActivityBroadcaster(),
                CreateChannelManager(CreateStreamingChannel().Object),
                Mock.Of<ISessionCompactor>(),
                new TestOptionsMonitor<CompactionOptions>(new CompactionOptions()),
                NullLogger<GatewayHost>.Instance,
                conversationStore: conversations);
        }

        public GatewayHost Host { get; }
        public Mock<IAgentSupervisor> Supervisor { get; }
        private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false }.ToString();

        public void Release() => _gate.TrySetResult();

        /// <summary>Waits, with a bounded poll, until the in-flight tool start has been saved, then forgets earlier supervisor calls.</summary>
        public async Task WaitForToolStartRowAsync()
        {
            await TestAwait.EventuallyAsync(
                async () => (await ReadStoredHistoryAsync()).Any(e => e.ToolCallId == "call-1" && e.IsToolStartRow()),
                "the in-flight tool-start row to be stored while the first turn is running",
                timeout: WaitLimit);

            // The host stops the handle once for a session with empty history (fresh system prompt).
            // That call belongs to the first turn, so only calls after this point are under test.
            Supervisor.Invocations.Clear();
        }

        public async Task<IReadOnlyList<SessionEntry>> ReadStoredHistoryAsync()
        {
            var session = await _store.GetAsync(SessionId.From("session-1"));
            return session?.GetHistorySnapshot() ?? [];
        }

        private async IAsyncEnumerable<AgentStreamEvent> GatedToolStream([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            _turnRunning = true;
            yield return new AgentStreamEvent
            {
                Type = AgentStreamEventType.ToolStart,
                ToolCallId = "call-1",
                ToolName = "slow_tool",
                ToolArgs = new Dictionary<string, object?> { ["path"] = "a.md" }
            };
            await _gate.Task.WaitAsync(WaitLimit, ct);
            yield return new AgentStreamEvent { Type = AgentStreamEventType.ToolEnd, ToolCallId = "call-1", ToolName = "slow_tool", ToolResult = "done", ToolIsError = false };
            yield return new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "finished" };
            yield return new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd, FinalContent = "finished" };
            _turnRunning = false;
        }

        public async ValueTask DisposeAsync()
        {
            Release();
            await Host.DisposeAsync();
            SqlitePoolCleanup.ClearPoolForConnectionString(ConnectionString);
            try { File.Delete(_dbPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* A lingering file lock must not fail the run. */ }
        }
    }

    private static Mock<IChannelAdapter> CreateStreamingChannel()
    {
        var channel = new Mock<IChannelAdapter>();
        channel.SetupGet(c => c.ChannelType).Returns("web");
        channel.SetupGet(c => c.DisplayName).Returns("web");
        channel.SetupGet(c => c.SupportsStreaming).Returns(true);
        channel.SetupGet(c => c.SupportsThinkingDisplay).Returns(false);
        channel.Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        channel.Setup(c => c.SendStreamDeltaAsync(It.IsAny<ChannelStreamTarget>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return channel;
    }

    private static IChannelManager CreateChannelManager(IChannelAdapter adapter)
    {
        var manager = new Mock<IChannelManager>();
        manager.SetupGet(m => m.Adapters).Returns([adapter]);
        manager.Setup(m => m.Get(It.IsAny<ChannelKey>())).Returns((ChannelKey channelType) =>
            channelType.Equals(adapter.ChannelType) ? adapter : null);
        manager.Setup(m => m.Get(It.IsAny<ChannelKey>(), It.IsAny<string?>())).Returns((ChannelKey channelType, string? _) =>
            channelType.Equals(adapter.ChannelType) ? adapter : null);
        return manager.Object;
    }

    private static InboundMessage CreateMessage(string content, CitizenId sender)
        => new()
        {
            ChannelType = "web",
            SenderId = "sender-1",
            Sender = sender,
            ChannelAddress = ChannelAddress.From("conv-1"),
            Content = content,
            RoutingHints = InboundMessageRoutingHints.LiftFromStrings(null, "session-1", null),
            Metadata = new Dictionary<string, object?>()
        };

    private sealed class NullActivityBroadcaster : IActivityBroadcaster
    {
        public ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public IAsyncEnumerable<GatewayActivity> SubscribeAsync(CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<GatewayActivity>();
    }
}
