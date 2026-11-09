using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Tests.TestInfrastructure;

/// <summary>One inbound message the orchestrator was asked to admit, and whether it was.</summary>
/// <param name="Message">The message exactly as the producer built it.</param>
/// <param name="IsReplay">True when the producer flagged it as a recovery replacement.</param>
/// <param name="Accepted">True when the orchestrator admitted it (accepted or steered).</param>
internal sealed record RecordedPost(InboundMessage Message, bool IsReplay, bool Accepted);

/// <summary>
/// Decorates the real inbound orchestrator to record every message producers hand it. Recovery
/// replacements are recognised by the replay flag they already carry in their metadata, so no test
/// has to know how recovery builds its message.
/// </summary>
/// <remarks>
/// <para>
/// A test can hold replay admissions at the door (<see cref="HoldReplayPosts"/>) to open a window in
/// which recovery has decided to admit but has not yet finished, then release them. Holding blocks
/// the posting thread, which is the point: it keeps the recovery routine suspended mid-decision.
/// </para>
/// <para>
/// A test can also make the orchestrator refuse replacements (<see cref="RefuseReplayPosts"/>), as a
/// full or closed queue would. A refused replacement never becomes a turn, so only
/// <see cref="AcceptedReplayPosts"/> says what was admitted and what a test must wait for.
/// </para>
/// </remarks>
internal sealed class RecordingOrchestrator(IInboundMessageOrchestrator inner) : IInboundMessageOrchestrator
{
    internal const string ReplayMetadataKey = "isReplay";

    private static readonly TimeSpan HoldLiveness = TimeSpan.FromSeconds(60);

    private readonly object _sync = new();
    private readonly List<RecordedPost> _posts = [];
    private readonly ManualResetEventSlim _replayGate = new(initialState: true);
    private readonly TaskCompletionSource _replayHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _refuseReplays;

    /// <summary>Every message handed to the orchestrator, in completion order.</summary>
    internal IReadOnlyList<RecordedPost> Posts
    {
        get { lock (_sync) return [.. _posts]; }
    }

    /// <summary>Every recovery replacement offered to the orchestrator, admitted or refused, in completion order.</summary>
    internal IReadOnlyList<RecordedPost> ReplayPosts => [.. Posts.Where(post => post.IsReplay)];

    /// <summary>The recovery replacements the orchestrator admitted. Only these ever run as turns.</summary>
    internal IReadOnlyList<RecordedPost> AcceptedReplayPosts => [.. ReplayPosts.Where(post => post.Accepted)];

    /// <summary>True once a replay admission has reached a closed gate.</summary>
    internal bool ReplayPostHeld => _replayHeld.Task.IsCompleted;

    /// <summary>Makes replay admissions wait at the door until <see cref="ReleaseReplayPosts"/>.</summary>
    internal void HoldReplayPosts() => _replayGate.Reset();

    /// <summary>Lets held and future replay admissions through. Safe to call when nothing is held.</summary>
    internal void ReleaseReplayPosts() => _replayGate.Set();

    /// <summary>
    /// Makes <see cref="Post"/> and <see cref="PostAsync"/>, the lanes recovery uses, refuse every recovery
    /// replacement from now on. Refusals are still recorded, as not accepted.
    /// </summary>
    internal void RefuseReplayPosts() => _refuseReplays = true;

    /// <inheritdoc />
    public async Task<InboundDispatchResult> AcceptAsync(InboundMessage message, CancellationToken cancellationToken = default)
    {
        var result = await inner.AcceptAsync(message, cancellationToken).ConfigureAwait(false);
        Record(message, accepted: result.Status is InboundDispatchStatus.Accepted or InboundDispatchStatus.Steered);
        return result;
    }

    /// <inheritdoc />
    public bool Post(InboundMessage message)
    {
        AwaitReplayGate(message);
        var accepted = !IsRefused(message) && inner.Post(message);
        Record(message, accepted);
        return accepted;
    }

    /// <inheritdoc />
    public async Task<InboundDispatchStatus> PostAsync(InboundMessage message, CancellationToken cancellationToken = default)
    {
        if (IsReplay(message) && !_replayGate.IsSet)
            await Task.Run(() => AwaitReplayGate(message), cancellationToken).ConfigureAwait(false);

        var status = IsRefused(message)
            ? InboundDispatchStatus.Busy
            : await inner.PostAsync(message, cancellationToken).ConfigureAwait(false);
        Record(message, accepted: status is InboundDispatchStatus.Accepted or InboundDispatchStatus.Steered);
        return status;
    }

    private static bool IsReplay(InboundMessage message)
        => message.Metadata.TryGetValue(ReplayMetadataKey, out var flag) && flag is true;

    private bool IsRefused(InboundMessage message) => _refuseReplays && IsReplay(message);

    private void AwaitReplayGate(InboundMessage message)
    {
        if (!IsReplay(message) || _replayGate.IsSet)
            return;

        _replayHeld.TrySetResult();
        if (!_replayGate.Wait(HoldLiveness))
            throw new TimeoutException("A held replay admission was never released.");
    }

    private void Record(InboundMessage message, bool accepted)
    {
        lock (_sync)
            _posts.Add(new RecordedPost(message, IsReplay(message), accepted));
    }
}

/// <summary>
/// Decorates the activity broadcaster to record every activity the gateway publishes and to count
/// finished turns, which is the harness's explicit completion signal (disposal is never a barrier).
/// </summary>
internal sealed class RecordingActivityBroadcaster(IActivityBroadcaster inner) : IActivityBroadcaster
{
    private readonly object _sync = new();
    private readonly List<GatewayActivity> _items = [];

    /// <summary>Every activity published so far, in order.</summary>
    internal IReadOnlyList<GatewayActivity> Items
    {
        get { lock (_sync) return [.. _items]; }
    }

    /// <summary>
    /// Turns that have ended. The gateway publishes one terminal activity per processed message, after
    /// everything the turn does inline (including lifecycle subscribers), so once a turn is counted
    /// here its recovery decisions are final.
    /// </summary>
    internal int TurnsFinished
    {
        get
        {
            lock (_sync)
                return _items.Count(item => item.Type is GatewayActivityType.AgentCompleted or GatewayActivityType.Error);
        }
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default)
    {
        lock (_sync)
            _items.Add(activity);

        return inner.PublishAsync(activity, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<GatewayActivity> SubscribeAsync(CancellationToken cancellationToken = default)
        => inner.SubscribeAsync(cancellationToken);
}

/// <summary>
/// A shared monotonic counter that gives events seen by different recorders one total order, so a test
/// can say "this save completed before that event was published" without relying on wall-clock time.
/// </summary>
internal sealed class HarnessClock
{
    private int _ticks;

    /// <summary>The next position in the order. Strictly increasing across threads.</summary>
    internal int Next() => Interlocked.Increment(ref _ticks);
}

/// <summary>A session lifecycle event with the session state as it was when the event was published.</summary>
/// <param name="Sequence">Position on the harness clock at which the event reached the recorder.</param>
/// <param name="Event">The published event.</param>
/// <param name="HistoryAtPublish">A copy of the session history at the moment of publication.</param>
/// <param name="RunCompletionAtPublish">The run completion recorded on the session at that moment.</param>
internal sealed record RecordedLifecycleEvent(
    int Sequence,
    SessionLifecycleEvent Event,
    IReadOnlyList<SessionEntry> HistoryAtPublish,
    RunCompletionSignal? RunCompletionAtPublish)
{
    /// <summary>True when the session still carried a crash sentinel when the event was published.</summary>
    internal bool HadSentinelAtPublish => HistoryAtPublish.Any(entry => entry.IsCrashSentinel);
}

/// <summary>
/// Records session lifecycle events. Subscribe it before any other subscriber so it sees each event
/// before the recovery routine mutates the session in response.
/// </summary>
internal sealed class RecordingLifecycleSubscriber
{
    private readonly SessionLifecycleEvents _events;
    private readonly HarnessClock _clock;
    private readonly object _sync = new();
    private readonly List<RecordedLifecycleEvent> _items = [];

    internal RecordingLifecycleSubscriber(SessionLifecycleEvents events, HarnessClock clock)
    {
        _events = events;
        _clock = clock;
        _events.SessionChanged += OnSessionChanged;
    }

    /// <summary>Every event published so far, in order.</summary>
    internal IReadOnlyList<RecordedLifecycleEvent> Events
    {
        get { lock (_sync) return [.. _items]; }
    }

    /// <summary>Number of recorded events of <paramref name="type"/>.</summary>
    internal int Count(SessionLifecycleEventType type) => Events.Count(item => item.Event.Type == type);

    /// <summary>Publishes a recorded event again, exactly as a duplicate or stale delivery would arrive.</summary>
    internal Task RepublishAsync(RecordedLifecycleEvent recorded) => _events.PublishAsync(recorded.Event);

    /// <summary>Stops recording.</summary>
    internal void Detach() => _events.SessionChanged -= OnSessionChanged;

    private Task OnSessionChanged(SessionLifecycleEvent lifecycleEvent, CancellationToken cancellationToken)
    {
        var session = lifecycleEvent.Session;
        var recorded = new RecordedLifecycleEvent(
            _clock.Next(),
            lifecycleEvent,
            session?.GetHistorySnapshot() ?? [],
            session?.RunCompletion);
        lock (_sync)
            _items.Add(recorded);

        return Task.CompletedTask;
    }
}

/// <summary>One post-run fan-out the host offered to the outbound deliverer.</summary>
/// <param name="SessionId">The session the run belonged to.</param>
/// <param name="Content">The reply text the host offered; null or empty means it offered nothing to deliver.</param>
internal sealed record RecordedFanOut(SessionId SessionId, string? Content);

/// <summary>
/// Stands in for the outbound fan-out deliverer and records what the host offers it after each run. The
/// real deliverer returns immediately for a null or empty reply, so what matters, and what is recorded,
/// is whether the host offered any reply text at all.
/// </summary>
internal sealed class RecordingDeliverer : IOutboundResponseDeliverer
{
    private readonly object _sync = new();
    private readonly List<RecordedFanOut> _items = [];

    /// <summary>Every fan-out the host offered, in order.</summary>
    internal IReadOnlyList<RecordedFanOut> FanOuts
    {
        get { lock (_sync) return [.. _items]; }
    }

    /// <inheritdoc />
    public Task FanOutAsync(
        InboundMessage source,
        SessionId sessionId,
        string? content,
        ConversationId conversationId,
        CancellationToken ct)
    {
        lock (_sync)
            _items.Add(new RecordedFanOut(sessionId, content));

        return Task.CompletedTask;
    }
}
