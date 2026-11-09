using BotNexus.Domain.Primitives;
using BotNexus.Domain.Text;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Concurrency;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Dispatching;
using BotNexus.Gateway.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Hosted service that runs once at gateway startup to detect and notify users whose
/// agent turn was interrupted by a gateway restart. Any session that contains an
/// unresolved crash-sentinel entry (written by <see cref="GatewayHost"/> before each
/// LLM call) indicates the previous run did not complete cleanly. For each such session
/// this service appends a <see cref="MessageRole.Notification"/> entry, removes the
/// sentinels, persists the session, and publishes that committed session-item fact through
/// the channel-neutral conversation event seam.
/// </summary>
/// <remarks>
/// Interactive sessions are replayed when <see cref="GatewayOptions.AutoReplayInterruptedTurns"/>
/// is <c>true</c>. Agent-only conversations are also replayed when the option is <c>false</c>
/// because no human participant can act on a resend notification. Cron/soul/subagent sessions
/// remain excluded. A replay counter in session metadata caps retries at
/// <see cref="GatewayOptions.MaxAutoReplayAttempts"/> to prevent infinite crash loops.
/// </remarks>
/// <remarks>
/// Conversation-event publication is deliberately post-commit and best-effort. A rejection or
/// exception is logged but never rolls back the saved notification, and this startup scan does not
/// retry publication because the crash sentinel has already been consumed. The session store is the
/// source of truth: clients recover missed live events by hydrating the persisted transcript, while
/// broader startup reconciliation belongs at the session/conversation projection boundary rather
/// than in this one-shot recovery service. <see cref="GatewayActivity"/> remains telemetry only.
/// </remarks>
/// <remarks>
/// The same recovery also runs live, when an agent-only API run settles as failed in this process
/// (<see cref="SessionLifecycleEventType.TerminalFailure"/>). The two are told apart by
/// <see cref="RecoveryCause"/>: a restart notice is true whatever admission decides, so the startup scan
/// writes it first and its texts never change. A run-failure notice states the failure and then what
/// admission is doing about it, and never claims a restart: it is saved with the reservation as queued,
/// and if the post is then refused the one restoring save replaces it with a not-queued report.
/// </remarks>
public sealed class InterruptedTurnNotificationService : IHostedLifecycleService
{
    internal const string NotificationContent =
        "⚠️ The gateway was restarted while your last message was being processed. " +
        "Your message was saved — please resend it to continue.";

    internal const string AgentOnlyNotificationContent =
        "⚠️ The gateway was restarted while the last message was being processed. " +
        "The message was saved; no human action is required.";

    internal const string MetadataKeyReplayCount = "interruption_replay_count";

    /// <summary>
    /// Gateway-authored banner (#3046) prepended to the session as a <see cref="MessageRole.System"/>
    /// entry flagged <see cref="SessionEntry.IsReplayBanner"/> immediately before an interrupted turn is
    /// auto-replayed. <c>{0}</c> is the replay attempt number, <c>{1}</c> the configured maximum, and
    /// <c>{2}</c> the original user message verbatim.
    /// </summary>
    /// <remarks>
    /// The wording deliberately refuses to claim the prior work was incomplete. The gateway knows only
    /// that the process died mid-turn, not how far the turn got; asserting "you did not finish" invites
    /// the agent to redo steps that already applied, which for a side-effecting turn (a file mutation, a
    /// revert, a push) is exactly the damage this banner exists to prevent. Stating the completion state
    /// as UNKNOWN and requiring verification closes both the "already done" and the "do it twice" reading.
    /// </remarks>
    internal const string ReplayBannerTemplate =
        "[PLATFORM RESTART - AUTOMATIC REPLAY {0} of {1}]\n" +
        "The gateway restarted while your previous turn was still running, so that turn was cut off at an " +
        "unknown point. Some of the work described above may have completed and some may not have. Do not " +
        "assume either. Before taking any action - especially anything that writes, mutates, or is otherwise " +
        "not safely repeatable - verify the current state directly with your tools, then continue from " +
        "wherever you actually are.\n\n" +
        "This is the original message that started the interrupted loop, repeated verbatim below:\n\n{2}";

    /// <summary>Builds the restart-replay banner for a given attempt and original message.</summary>
    internal static string BuildReplayBanner(string originalContent, int attempt, int maxAttempts)
        => string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            ReplayBannerTemplate,
            attempt,
            maxAttempts,
            originalContent);

    /// <summary>
    /// Banner prepended before a replacement admitted after a LIVE run failure. It keeps the discipline of
    /// <see cref="ReplayBannerTemplate"/> (the completion state is unknown, so verify before repeating
    /// anything that writes) and states only what the gateway knows: the run ended with an error. Nothing
    /// restarted, so it must never say so. <c>{0}</c> is the attempt number, <c>{1}</c> the configured
    /// maximum and <c>{2}</c> the original message verbatim.
    /// </summary>
    internal const string RunFailureReplayBannerTemplate =
        "[AUTOMATIC RECOVERY {0} of {1}]\n" +
        "Your previous turn ended with an error, so that turn was cut off at an unknown point. Some of the " +
        "work described above may have completed and some may not have. Do not assume either. Before " +
        "taking any action - especially anything that writes, mutates, or is otherwise not safely " +
        "repeatable - verify the current state directly with your tools, then continue from wherever you " +
        "actually are.\n\n" +
        "This is the original message that started the interrupted loop, repeated verbatim below:\n\n{2}";

    /// <summary>Builds the live run-failure banner for a given attempt and original message.</summary>
    internal static string BuildRunFailureBanner(string originalContent, int attempt, int maxAttempts)
        => string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            RunFailureReplayBannerTemplate,
            attempt,
            maxAttempts,
            originalContent);

    private const int MaxFailureDetailLength = 240;
    private const string Ellipsis = "...";
    private const string RunFailureNoticeLead = "The previous agent run ended with an error";
    private const string ResumeHint = "Post a message to the conversation to resume it.";

    private readonly ISessionStore _sessions;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IActivityBroadcaster _broadcaster;
    private readonly IConversationEventPublisher _eventPublisher;
    private readonly ILogger<InterruptedTurnNotificationService> _logger;
    private readonly IInboundMessageOrchestrator? _orchestrator;
    private readonly GatewayOptions _options;
    private readonly IConversationStore? _conversations;
    private readonly SessionLifecycleEvents? _lifecycleEvents;
    private readonly CleanShutdownMarker? _shutdownMarker;
    private readonly ISessionTurnTracker? _turnTracker;

    // One gate per session id, shared by the live handler and the startup scan, so the two can never
    // reason about the same session's leases at once.
    private readonly StripedAsyncLock _gates = new();

    // Names this instance on the replacements it queues. A reservation stamped with another token was
    // queued by a process that is gone, so its replacement no longer exists and must be queued again.
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Initializes the startup recovery scan and its post-commit conversation-event projection.
    /// </summary>
    public InterruptedTurnNotificationService(
        ISessionStore sessions,
        IAgentRegistry agentRegistry,
        IActivityBroadcaster broadcaster,
        IConversationEventPublisher eventPublisher,
        ILogger<InterruptedTurnNotificationService> logger,
        IInboundMessageOrchestrator? orchestrator,
        IOptions<GatewayOptions>? options,
        IConversationStore? conversations = null,
        SessionLifecycleEvents? lifecycleEvents = null,
        CleanShutdownMarker? shutdownMarker = null,
        ISessionTurnTracker? turnTracker = null)
    {
        _sessions = sessions;
        _agentRegistry = agentRegistry;
        _broadcaster = broadcaster;
        _eventPublisher = eventPublisher;
        _logger = logger;
        _orchestrator = orchestrator;
        _options = options?.Value ?? new GatewayOptions();
        _conversations = conversations;
        _lifecycleEvents = lifecycleEvents;
        _shutdownMarker = shutdownMarker;
        _turnTracker = turnTracker;
    }

    /// <inheritdoc />
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// No-op at StartAsync: the interrupted-turn scan is deferred to <see cref="StartedAsync"/>.
    /// The scan iterates <see cref="IAgentRegistry.GetAll"/>, but agents are registered by other
    /// hosted services (e.g. AgentConfigurationHostedService, ConfigHydrationService (formerly BuiltInAgentRegistrationService)/AgentConfigurationHostedService)
    /// during their own StartAsync. Running the scan here races that registration and historically
    /// always observed an empty registry, so the scan silently no-opped and orphaned sentinels
    /// survived every restart (#2030). StartedAsync runs only after every hosted service's
    /// StartAsync has completed, so the registry is fully populated by then.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_lifecycleEvents is not null)
            _lifecycleEvents.SessionChanged += OnSessionLifecycleChangedAsync;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        const int pageSize = 256;
        var registeredAgents = _agentRegistry.GetAll().Select(static descriptor => descriptor.AgentId).ToHashSet();
        var notified = 0;
        var replayed = 0;
        var scanned = 0;
        var terminalSkipped = 0;
        var liveSkipped = 0;
        var fenceRejected = 0;
        var persistenceFailed = 0;
        string? cursor = null;
        var started = Stopwatch.StartNew();
        var allocatedBefore = GC.GetTotalAllocatedBytes();

        try
        {
            do
            {
                var page = await _sessions.ListUnresolvedCrashSentinelsAsync(pageSize, cursor, cancellationToken)
                    .ConfigureAwait(false);
                scanned += page.Rows.Count;
                foreach (var row in page.Rows)
                {
                    if (!registeredAgents.Contains(row.AgentId))
                        continue;

                    try
                    {
                        // The same per-session gate the live handler takes, so a failure that is being
                        // recovered right now and this scan can never both decide about one session.
                        using var gate = await _gates.AcquireAsync(row.SessionId.Value, cancellationToken)
                            .ConfigureAwait(false);

                        // Reload under the gate: the candidate row was read before the gate was held, and
                        // whatever the live handler did meanwhile is only visible in the reloaded session. The
                        // gate does not keep the host out, though: the residual race with a brand-new inbound turn
                        // is described where the reservation is saved (QueueReplacementAsync).
                        var session = await _sessions.GetAsync(row.SessionId, cancellationToken).ConfigureAwait(false);
                        if (session is null || !session.History.Any(static entry => entry.IsCrashSentinel))
                            continue;

                        // The session may have become terminal since the candidate page was read.
                        // Do not resolve participants or mutate the hydrated transcript in that case.
                        if (SessionMutationPolicy.IsTerminal(session.Status))
                        {
                            terminalSkipped++;
                            continue;
                        }

                        // A turn that is running right now owns its sentinel. It is not an interrupted turn.
                        if (_turnTracker?.HasLiveTurn(row.SessionId.Value) == true)
                        {
                            liveSkipped++;
                            continue;
                        }

                        _logger.LogInformation(
                            "Session {SessionId} (agent {AgentId}) has unresolved crash sentinels - notifying user",
                            row.SessionId.Value, row.AgentId.Value);
                        var isAgentOnlyConversation = await IsAgentOnlyConversationAsync(session, cancellationToken)
                            .ConfigureAwait(false);
                        var outcome = session.History.Any(static entry => entry.IsCrashSentinel && entry.RunLease is not null)
                            ? await RecoverLeasedSessionAsync(session, row.AgentId, isAgentOnlyConversation, cancellationToken)
                                .ConfigureAwait(false)
                            : await RecoverUnleasedSessionAsync(session, row.AgentId, isAgentOnlyConversation, cancellationToken)
                                .ConfigureAwait(false);
                        if (outcome.ReplayAccepted) replayed++;
                        switch (outcome.Persistence)
                        {
                            case RecoveryPersistence.Persisted:
                                notified++;
                                break;
                            case RecoveryPersistence.FenceRejected:
                                fenceRejected++;
                                break;
                            case RecoveryPersistence.Failed:
                                persistenceFailed++;
                                break;
                        }
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(
                            ex,
                            "Interrupted-turn recovery failed for session {SessionId} (agent {AgentId}); continuing with later candidates",
                            row.SessionId.Value, row.AgentId.Value);
                    }
                }
                cursor = page.NextCursor;
            } while (cursor is not null);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed during bounded interrupted-turn scan after {ScannedRows} candidate row(s)", scanned);
        }

        _logger.LogInformation(
            "Interrupted-turn scan complete: {ScannedRows} candidate row(s), {ElapsedMilliseconds} ms, {AllocatedBytes} allocated bytes; " +
            "{NotifiedCount} session(s) notified ({ReplayedCount} auto-replayed), {TerminalSkippedCount} terminal skipped, " +
            "{LiveSkippedCount} with a live turn skipped, {FenceRejectedCount} fence rejected, {PersistenceFailedCount} persistence failed",
            scanned, started.ElapsedMilliseconds, GC.GetTotalAllocatedBytes() - allocatedBefore,
            notified, replayed, terminalSkipped, liveSkipped, fenceRejected, persistenceFailed);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifecycleEvents is not null)
            _lifecycleEvents.SessionChanged -= OnSessionLifecycleChangedAsync;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task OnSessionLifecycleChangedAsync(
        SessionLifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken)
    {
        // Only a failure that names the lease of the run that failed can be handled race-safely. The event
        // fixed that identity when it was published, so however late it is handled, and however many runs
        // have started on the session since, it can only ever concern that one lease.
        if (lifecycleEvent.Type != SessionLifecycleEventType.TerminalFailure
            || lifecycleEvent.LeaseId is not { } leaseId
            || lifecycleEvent.Session is not { } published
            || published.ChannelType != ChannelKey.From("api"))
        {
            return;
        }

        using var gate = await _gates.AcquireAsync(lifecycleEvent.SessionId, cancellationToken).ConfigureAwait(false);

        // Reload under the gate. The event carries a reference to the live session, which says nothing
        // about what happened to it since the event was published.
        var session = await _sessions.GetAsync(published.SessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
            return;

        // The lease is the identity. When it is not on the session, or no longer failed, this event was
        // already handled (its lease consumed or superseded by a replacement) or belongs to a run whose
        // outcome is not this handler's to decide. Nothing is read, written or admitted.
        if (session.FindRunLease(leaseId) is not { State: RunLeaseState.Failed } lease)
            return;

        if (!session.IsInteractive
            || !await IsAgentOnlyConversationAsync(session, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await RecoverLeaseAsync(
                session, session.AgentId, isAgentOnlyConversation: true, lease, RecoveryCause.RunFailure,
                new RestartNoticeState(), cancellationToken)
            .ConfigureAwait(false);
    }

    private enum RecoveryPersistence
    {
        Persisted,
        FenceRejected,
        Failed
    }

    // A replay that was accepted stays accepted even if the save that reports it fails. A leased recovery
    // persists its reservation before posting; the older, lease-less path posts first.
    private readonly record struct RecoveryOutcome(bool ReplayAccepted, RecoveryPersistence Persistence);

    private enum ReplayStatus
    {
        /// <summary>Recovery did not try to replay: replay is not enabled or not possible for this session.</summary>
        NotAttempted,

        /// <summary>The orchestrator accepted the replacement. Acceptance only enqueues it.</summary>
        Queued,

        /// <summary>The input already spent its replay budget, so no replacement was offered.</summary>
        AttemptsExhausted,

        /// <summary>No authoritative initiating message exists to replay, so no replacement was offered.</summary>
        NoInitiatingInput,

        /// <summary>The orchestrator refused the replacement. The budget is untouched.</summary>
        AdmissionRefused,

        /// <summary>There is no orchestrator to admit a replacement. Nothing is consumed: a later boot may have one.</summary>
        OrchestratorUnavailable
    }

    private readonly record struct ReplayAttempt(
        ReplayStatus Status,
        int Attempt = 0,
        string InitiatingContent = "",
        string[]? OutcomeUnknownToolCallIds = null)
    {
        internal static ReplayAttempt None => new(ReplayStatus.NotAttempted);
    }

    // The restart notice states a fact about the process, so a scan that recovers several leases in one
    // session says it once.
    private sealed class RestartNoticeState
    {
        internal bool Written { get; set; }
    }

    private async Task<RecoveryOutcome> RecoverUnleasedSessionAsync(
        GatewaySession session,
        AgentId agentId,
        bool isAgentOnlyConversation,
        CancellationToken cancellationToken)
    {
        var writeFence = SessionWriteFence.Capture(session);
        var shouldAttemptReplay = (_options.AutoReplayInterruptedTurns
                || isAgentOnlyConversation
                || _shutdownMarker?.PreviousShutdownWasPlanned == true)
            && session.IsInteractive
            && _orchestrator is not null;

        async Task<ReplayAttempt> AttemptReplayAsync()
            => shouldAttemptReplay
                ? await TryAutoReplayAsync(session, agentId, cancellationToken).ConfigureAwait(false)
                : ReplayAttempt.None;

        // A restart notice states a fact that holds whatever admission decides, so it is written before the
        // replay is attempted and the banner follows it.
        var restartNotice = isAgentOnlyConversation ? AgentOnlyNotificationContent : NotificationContent;
        var notification = AddNotification(session, restartNotice);
        var replay = await AttemptReplayAsync().ConfigureAwait(false);
        var activityMessage = replay.Status == ReplayStatus.Queued ? $"{restartNotice} (auto-replaying)" : restartNotice;

        var didReplay = replay.Status == ReplayStatus.Queued;
        if (didReplay)
            session.AddEntry(BuildBannerEntry(replay, RecoveryCause.GatewayRestart));

        // Sentinel retention for a sentinel that carries no lease is the rule it has always had: an accepted
        // replay consumes it, and so does a session replay does not apply to; anything else leaves the marker.
        if (didReplay || !shouldAttemptReplay)
            session.RemoveCrashSentinels();

        var persistence = await TrySaveAsync(session, writeFence, uncommitted: null, cancellationToken).ConfigureAwait(false);
        if (persistence != RecoveryPersistence.Persisted)
            return new RecoveryOutcome(didReplay, persistence);

        // The committed transcript remains authoritative even if post-commit telemetry fails.
        await PublishNoticeAsync(session, agentId, notification, activityMessage, cancellationToken).ConfigureAwait(false);
        return new RecoveryOutcome(didReplay, RecoveryPersistence.Persisted);
    }

    // The one fenced save every recovery step goes through. Whether it persisted is the caller's to act on:
    // a step that added rows or changed a lease takes them back when it did not. A save that cancellation cuts
    // short is no more known to have persisted than one that failed, so the step's edit, when it has one, is
    // taken back here before the cancellation propagates: memory never keeps what the store was not told.
    private async Task<RecoveryPersistence> TrySaveAsync(
        GatewaySession session,
        SessionWriteFence fence,
        SessionEdit? uncommitted,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await _sessions.SaveAsync(session, fence, cancellationToken).ConfigureAwait(false);
            if (outcome == SessionSaveOutcome.Persisted)
                return RecoveryPersistence.Persisted;

            _logger.LogInformation(
                "Interrupted-turn recovery for session {SessionId} was skipped because the session was deleted, sealed, or rebound",
                session.SessionId.Value);
            return RecoveryPersistence.FenceRejected;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to save session {SessionId} after interrupted-turn recovery", session.SessionId.Value);
            return RecoveryPersistence.Failed;
        }
        catch (Exception)
        {
            uncommitted?.Rollback();
            throw;
        }
    }

    // Announces a notice that is already committed: the conversation event for the persisted row, then the
    // activity. Both are best-effort; the transcript is the record.
    private async Task PublishNoticeAsync(
        GatewaySession session,
        AgentId agentId,
        SessionEntry notification,
        string activityMessage,
        CancellationToken cancellationToken)
    {
        await PublishPersistedNotificationAsync(session, agentId, notification, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _broadcaster.PublishAsync(new GatewayActivity
            {
                Type = GatewayActivityType.System,
                AgentId = agentId.Value,
                SessionId = session.SessionId.Value,
                ConversationId = session.ConversationId.IsInitialized() ? session.ConversationId.Value : null,
                Message = activityMessage
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Failed to publish activity for persisted interrupted-turn notification in session {SessionId}",
                session.SessionId.Value);
        }
    }

    // Changes made to the live session by one recovery step, kept so they can be taken back precisely. The
    // live aggregate is shared with whatever else runs on the session, so a save that does not persist must
    // not leave this step's rows or lease behind: a retry would then see them and conclude the work was
    // done. Each change is undone by identity, so nothing another writer added meanwhile is touched.
    private sealed class SessionEdit(GatewaySession session)
    {
        private readonly List<SessionEntry> _added = [];
        private readonly List<SessionEntry> _removed = [];
        private readonly List<(RunLeaseId Current, RunLease Previous)> _swapped = [];
        private readonly List<SessionEntry> _removedSentinels = [];

        internal SessionEntry Add(SessionEntry entry)
        {
            var stored = session.AddEntryAndSnapshot(entry).Snapshot.Entries[^1];
            _added.Add(stored);
            return stored;
        }

        // Takes a row out of the transcript by identity: the stored instance an earlier step added. A row
        // another writer added, even one that reads the same, is never touched.
        internal void Remove(SessionEntry stored)
        {
            if (session.TryRemoveEntry(stored))
                _removed.Add(stored);
        }

        internal RunLease? ReplaceLease(RunLeaseId leaseId, Func<RunLease, RunLease> change)
        {
            RunLease? previous = null;
            RunLease? replacement = null;
            session.TryUpdateRunLease(leaseId, lease =>
            {
                previous = lease;
                replacement = change(lease);
                return replacement;
            });

            if (previous is not null && replacement is not null && !ReferenceEquals(previous, replacement))
                _swapped.Add((replacement.LeaseId, previous));

            return replacement;
        }

        internal void RemoveSentinel(RunLeaseId leaseId)
        {
            if (session.TryRemoveCrashSentinel(leaseId, out var removed) && removed is not null)
                _removedSentinels.Add(removed);
        }

        internal void Rollback()
        {
            for (var index = _swapped.Count - 1; index >= 0; index--)
            {
                var (current, previous) = _swapped[index];
                session.TryUpdateRunLease(current, _ => previous);
            }

            foreach (var sentinel in _removedSentinels)
                session.AddEntry(sentinel);

            foreach (var stored in _added)
                session.TryRemoveEntry(stored);

            foreach (var row in _removed)
                session.AddEntry(row);

            _added.Clear();
            _removed.Clear();
            _swapped.Clear();
            _removedSentinels.Clear();
        }
    }

    // The replacement for an interrupted input: the input re-sent verbatim, as what it was, from the agent.
    // The sender is the agent itself and never a person, so replaying cannot add a human participant and
    // switch off agent-only recovery.
    private InboundMessage BuildReplacementMessage(
        GatewaySession session,
        AgentId agentId,
        SessionEntry origin,
        string[] outcomeUnknownToolCallIds,
        RecoveryContext? recovery)
    {
        var channelType = session.ChannelType ?? ChannelKey.From("internal");
        var callerId = session.CallerId ?? session.SessionId.Value;
        var metadata = new Dictionary<string, object?>
        {
            ["isReplay"] = true,
            ["originalTimestamp"] = origin.Timestamp.ToString("o")
        };
        if (outcomeUnknownToolCallIds.Length > 0)
            metadata["outcomeUnknownToolCallIds"] = outcomeUnknownToolCallIds;

        return new InboundMessage
        {
            ChannelType = channelType,
            SenderId = callerId,
            Sender = CitizenId.Of(agentId),
            ChannelAddress = ChannelAddress.From(session.ConversationId.IsInitialized()
                ? session.ConversationId.Value
                : callerId),
            Content = origin.Content,
            Timestamp = DateTimeOffset.UtcNow,
            Trigger = origin.Trigger,
            SpeakAs = origin.Role,
            Kind = origin.Kind,
            RoutingHints = new InboundMessageRoutingHints(
                RequestedAgentId: agentId,
                RequestedSessionId: session.SessionId,
                RequestedConversationId: session.ConversationId.IsInitialized() ? session.ConversationId : null),
            Metadata = metadata,
            Recovery = recovery
        };
    }

    private static SessionEntry AddNotification(GatewaySession session, string content)
    {
        var notification = new SessionEntry
        {
            Role = MessageRole.Notification,
            Content = content,
            Timestamp = DateTimeOffset.UtcNow
        };
        session.AddEntry(notification);
        return notification;
    }

    private async Task<RecoveryOutcome> RecoverLeasedSessionAsync(
        GatewaySession session,
        AgentId agentId,
        bool isAgentOnlyConversation,
        CancellationToken cancellationToken)
    {
        var restartNotice = new RestartNoticeState();
        var accepted = false;
        var persistence = RecoveryPersistence.Persisted;

        // Each leased sentinel is recovered on its own, in transcript order. One lease's fate never depends
        // on another's, and recovering one never touches another's sentinel.
        var leases = session.GetHistorySnapshot()
            .Where(static entry => entry.IsCrashSentinel)
            .Select(static entry => entry.RunLease)
            .OfType<RunLease>()
            .ToList();
        foreach (var listed in leases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Re-read it: recovering an earlier lease in this pass may have changed the session.
            if (session.FindRunLease(listed.LeaseId) is not { } lease)
                continue;

            // A reservation this instance queued is a replacement waiting its turn, not a lost one.
            if (lease.State == RunLeaseState.Pending && lease.QueuedBy == _instanceId)
                continue;

            // The lease says what happened. A failed one failed in this process's lifetime or an earlier
            // one's and was never recovered; a running or pending one belonged to a process that is gone.
            var cause = lease.State == RunLeaseState.Failed ? RecoveryCause.RunFailure : RecoveryCause.GatewayRestart;
            var outcome = await RecoverLeaseAsync(
                    session, agentId, isAgentOnlyConversation, lease, cause, restartNotice, cancellationToken)
                .ConfigureAwait(false);
            accepted |= outcome.ReplayAccepted;
            if (outcome.Persistence != RecoveryPersistence.Persisted)
                persistence = outcome.Persistence;
        }

        return new RecoveryOutcome(accepted, persistence);
    }

    /// <summary>
    /// Recovers ONE lease: decides whether its input is owed a replacement and, when it is, hands it over
    /// durably before posting. The order is the whole design: the reservation (a pending successor on the
    /// next attempt, with the old lease superseded) and the report that goes with it are saved first, in one
    /// save, so a crash at any later point leaves a reservation that startup re-queues without spending
    /// budget, and a replacement that fails at once already finds its attempt counted. Only then is the
    /// replacement posted, and nothing is saved after an accepted post. If the post is refused one save
    /// takes the optimistic report back, restores the old lease and reports the refusal, leaving the budget
    /// intact. Only this lease is ever touched, so a follow-up's, or a later run's, sentinel is never in play.
    /// </summary>
    private async Task<RecoveryOutcome> RecoverLeaseAsync(
        GatewaySession session,
        AgentId agentId,
        bool isAgentOnlyConversation,
        RunLease lease,
        RecoveryCause cause,
        RestartNoticeState restartNotice,
        CancellationToken cancellationToken)
    {
        var fence = SessionWriteFence.Capture(session);
        var policyAllowsReplay = (_options.AutoReplayInterruptedTurns
                || isAgentOnlyConversation
                || _shutdownMarker?.PreviousShutdownWasPlanned == true)
            && session.IsInteractive;
        var restartText = isAgentOnlyConversation ? AgentOnlyNotificationContent : NotificationContent;

        var (blocker, origin) = EvaluateReplay(session, lease, policyAllowsReplay);
        return origin is not null && blocker is null
            ? await QueueReplacementAsync(
                    session, agentId, lease, origin, cause, restartNotice, restartText, fence, cancellationToken)
                .ConfigureAwait(false)
            : await RecordBlockedAsync(
                    session, agentId, lease, cause, restartNotice, restartText,
                    blocker ?? ReplayStatus.NoInitiatingInput, fence, cancellationToken)
                .ConfigureAwait(false);
    }

    // What stands between this lease and a replacement. The first blocker decides; with none, the stored
    // input the lease names is what gets replayed.
    private (ReplayStatus? Blocker, SessionEntry? Origin) EvaluateReplay(
        GatewaySession session,
        RunLease lease,
        bool policyAllowsReplay)
    {
        if (!policyAllowsReplay)
            return (ReplayStatus.NotAttempted, null);

        if (_orchestrator is null)
            return (ReplayStatus.OrchestratorUnavailable, null);

        // A pending lease is a replacement that was already granted, so re-queuing it costs nothing.
        if (lease.State != RunLeaseState.Pending && lease.Attempt >= _options.MaxAutoReplayAttempts)
        {
            _logger.LogWarning(
                "Input of lease {LeaseId} in session {SessionId} has used all {Max} automatic recovery attempts; no replacement is queued",
                lease.LeaseId.Value, session.SessionId.Value, _options.MaxAutoReplayAttempts);
            return (ReplayStatus.AttemptsExhausted, null);
        }

        var resolution = lease.Origin.Resolve(session.GetHistorySnapshot());
        if (resolution.Entry is { } origin && !string.IsNullOrWhiteSpace(origin.Content))
            return (null, origin);

        _logger.LogWarning(
            "Lease {LeaseId} in session {SessionId} names no usable initiating message ({Match}); no replacement is queued and the marker is consumed",
            lease.LeaseId.Value, session.SessionId.Value, resolution.Match);
        return (ReplayStatus.NoInitiatingInput, null);
    }

    // No replacement will be queued for this lease. A blocker that cannot change by trying again (nothing to
    // replay, budget spent, replay not applicable here) is reported once and consumes the lease, so it is not
    // reported again at every later boot. One that can change (no orchestrator yet) keeps the lease for a
    // later boot to retry, and its report is recorded once per lease.
    private async Task<RecoveryOutcome> RecordBlockedAsync(
        GatewaySession session,
        AgentId agentId,
        RunLease lease,
        RecoveryCause cause,
        RestartNoticeState restartNotice,
        string restartText,
        ReplayStatus blocker,
        SessionWriteFence fence,
        CancellationToken cancellationToken)
    {
        var final = blocker != ReplayStatus.OrchestratorUnavailable;
        var text = cause == RecoveryCause.GatewayRestart
            ? restartText
            : BuildRunFailureNotice(
                FormatFailureDetail(lease.FailureDetail), new ReplayAttempt(blocker, lease.Attempt), _options.MaxAutoReplayAttempts);

        var edit = new SessionEdit(session);
        SessionEntry? notice = null;
        var writesRestartNotice = cause == RecoveryCause.GatewayRestart && !restartNotice.Written;
        if (writesRestartNotice || (cause == RecoveryCause.RunFailure && !ReportCommitted(session, lease, text)))
            notice = edit.Add(NewReport(text, lease));

        if (final)
            edit.RemoveSentinel(lease.LeaseId);

        if (notice is null && !final)
            return new RecoveryOutcome(false, RecoveryPersistence.Persisted);

        var saved = await TrySaveAsync(session, fence, edit, cancellationToken).ConfigureAwait(false);
        if (saved != RecoveryPersistence.Persisted)
        {
            // Nothing persisted, so nothing is claimed: the transcript and the lease go back exactly as they
            // were, and a retry starts from what is really stored.
            edit.Rollback();
            return new RecoveryOutcome(false, saved);
        }

        if (writesRestartNotice)
            restartNotice.Written = true;

        if (notice is not null)
            await PublishNoticeAsync(session, agentId, notice, text, cancellationToken).ConfigureAwait(false);

        return new RecoveryOutcome(false, RecoveryPersistence.Persisted);
    }

    private async Task<RecoveryOutcome> QueueReplacementAsync(
        GatewaySession session,
        AgentId agentId,
        RunLease lease,
        SessionEntry origin,
        RecoveryCause cause,
        RestartNoticeState restartNotice,
        string restartText,
        SessionWriteFence fence,
        CancellationToken cancellationToken)
    {
        var evidence = RecoveryEvidence.Collect(session.GetHistorySnapshot(), origin);
        var unknownIds = DistinctCallIds(evidence.OutcomeUnknown);

        // 1. Reserve. ONE save carries everything this recovery writes for the attempt: the old lease is
        //    superseded by a pending successor on the next attempt, and the report that goes with it (the
        //    restart notice or the failure notice, and the banner) is written in the same save, optimistically,
        //    as queued. Nothing is posted until it is durable, and nothing is saved once the post is accepted:
        //    from then on the replacement may be running on this very session, and a save from this routine's
        //    copy of it would put an older view over what the replacement has written since. A crash between
        //    this save and the post leaves a queued report beside a pending lease, and the next boot queues
        //    that lease, which makes the report true.
        //
        //    Residual race, documented rather than closed. The gate serializes this service's own paths (the
        //    live handler and the startup scan) but not the host, so a brand-new inbound turn can begin for the
        //    session between the reload that gave this routine its view of it and this save. A save built on
        //    that view then lands over the turn's session-level writes (its rows are appended, not lost). The
        //    startup scan has always had this window; it is the milliseconds between reload and save, and the
        //    live path cannot meet it because the host serializes a session's turns. The store's only
        //    conditional write, the content fingerprint on SessionWriteFence, does not fit: it compares the
        //    stored content with a fingerprint taken before an edit and is meant for editing a detached copy,
        //    while this routine edits the live aggregate in place and carries it through the later steps (the
        //    in-memory store's stored content IS that aggregate, so the comparison could never pass).
        var reservation = new SessionEdit(session);
        var reserved = reservation.ReplaceLease(
            lease.LeaseId,
            current => current.State == RunLeaseState.Pending
                ? current.Requeue(_instanceId)
                : current.QueueSuccessor(_instanceId));
        if (reserved is null)
        {
            // The lease is gone: another path already consumed it. There is nothing left to decide.
            return new RecoveryOutcome(false, RecoveryPersistence.Persisted);
        }

        var replay = new ReplayAttempt(ReplayStatus.Queued, reserved.Attempt, origin.Content, unknownIds);
        var writesRestartNotice = cause == RecoveryCause.GatewayRestart && !restartNotice.Written;
        var queuedText = cause == RecoveryCause.GatewayRestart
            ? restartText
            : BuildRunFailureNotice(FormatFailureDetail(lease.FailureDetail), replay, _options.MaxAutoReplayAttempts);
        SessionEntry? restartRow = writesRestartNotice ? reservation.Add(NewReport(restartText, lease)) : null;
        SessionEntry? queuedRow = cause == RecoveryCause.RunFailure ? reservation.Add(NewReport(queuedText, lease)) : null;
        var bannerRow = reservation.Add(BuildBannerEntry(replay, cause) with { AgentRunId = lease.FailedRunId });

        var reservationSaved = await TrySaveAsync(session, fence, reservation, cancellationToken).ConfigureAwait(false);
        if (reservationSaved != RecoveryPersistence.Persisted)
        {
            reservation.Rollback();
            return new RecoveryOutcome(false, reservationSaved);
        }

        if (writesRestartNotice)
            restartNotice.Written = true;

        // Admission is checked for cancellation once the reservation is safe: a shutdown here leaves it for
        // the next boot to queue, and that costs no budget.
        cancellationToken.ThrowIfCancellationRequested();

        // 2. Post. The replacement carries the reservation it will take over and what the interrupted work
        //    is known to have done. Acceptance only enqueues it.
        var replacement = BuildReplacementMessage(
            session, agentId, origin, unknownIds,
            new RecoveryContext
            {
                SuccessorLeaseId = reserved.LeaseId,
                Attempt = reserved.Attempt,
                MaxAttempts = _options.MaxAutoReplayAttempts,
                CompletedCalls = evidence.Completed,
                OutcomeUnknownCalls = evidence.OutcomeUnknown
            });
        var accepted = false;
        try
        {
            accepted = _orchestrator!.Post(replacement);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Posting the recovery replacement for session {SessionId} failed", session.SessionId.Value);
        }

        _logger.LogInformation(
            "Auto-replay for session {SessionId}: Post returned {Accepted} (attempt {Attempt}/{Max}, outcome-unknown tools {OutcomeUnknownCount})",
            session.SessionId.Value, accepted, reserved.Attempt, _options.MaxAutoReplayAttempts, unknownIds.Length);

        // 3a. Refused. ONE save puts everything back: the optimistic report goes (by identity), the old lease
        //     returns, so the budget is exactly as it was, and, for a failure, the refusal is reported. If that
        //     save does not persist, or cancellation cuts it short, the durable state stays what the
        //     reservation save made it, which is the state a crash right here would have left, and the next boot
        //     queues the reservation without spending anything. The in-memory session then goes back to match
        //     it, so nothing uncommitted is ever mistaken for a committed report.
        if (!accepted)
        {
            var restore = new SessionEdit(session);
            if (queuedRow is not null)
                restore.Remove(queuedRow);

            restore.Remove(bannerRow);
            restore.ReplaceLease(reserved.LeaseId, _ => lease);

            SessionEntry? refusalRow = null;
            if (cause == RecoveryCause.RunFailure)
            {
                var refusalText = BuildRunFailureNotice(
                    FormatFailureDetail(lease.FailureDetail),
                    new ReplayAttempt(ReplayStatus.AdmissionRefused, lease.Attempt),
                    _options.MaxAutoReplayAttempts);
                if (!ReportCommitted(session, lease, refusalText))
                    refusalRow = restore.Add(NewReport(refusalText, lease));
            }

            var restored = await TrySaveAsync(session, fence, restore, cancellationToken).ConfigureAwait(false);
            if (restored != RecoveryPersistence.Persisted)
            {
                restore.Rollback();
                _logger.LogWarning(
                    "The refused reservation for session {SessionId} could not be restored durably; the next boot queues it without spending the attempt",
                    session.SessionId.Value);
                return new RecoveryOutcome(false, restored);
            }

            // For a restart the notice persisted with the reservation is still true whatever admission decided.
            var refusal = refusalRow ?? restartRow;
            if (refusal is not null)
                await PublishNoticeAsync(session, agentId, refusal, refusal.Content, cancellationToken).ConfigureAwait(false);

            return new RecoveryOutcome(false, RecoveryPersistence.Persisted);
        }

        // 3b. Accepted. The report is already durable, written with the reservation, so there is nothing left
        //     to save and nothing is saved. Announcing it is not a session write.
        var announced = restartRow ?? queuedRow;
        if (announced is not null)
        {
            var activity = cause == RecoveryCause.GatewayRestart ? $"{queuedText} (auto-replaying)" : queuedText;
            await PublishNoticeAsync(session, agentId, announced, activity, cancellationToken).ConfigureAwait(false);
        }

        return new RecoveryOutcome(true, RecoveryPersistence.Persisted);
    }

    // A row recovery writes about a failed run. It carries that run's id, so the report is tied to the run it
    // is about by a field every store persists, and a later failure whose report happens to read the same is
    // never taken for it.
    private static SessionEntry NewReport(string content, RunLease lease) => new()
    {
        Role = MessageRole.Notification,
        Content = content,
        Timestamp = DateTimeOffset.UtcNow,
        AgentRunId = lease.FailedRunId
    };

    // True when this failed run already has exactly this report on the transcript. Only the run id ties a
    // report to a run: matching on the words alone would let one failure's report stand in for another's
    // that reads the same. Everything a recovery step adds to the live session is taken back when its save
    // does not persist, so a row found here was committed.
    //
    // A lease that recorded no run id matches nothing, so for a lease that is kept (no orchestrator) a
    // repeated signal can write its report again. That one gap is left open on purpose. A settled run reports
    // its id, so it is not expected in practice. No persisted row field is free to carry the lease's own id as
    // a fallback: AgentRunId names a run, PersistenceKey is the store's duplicate guard for tool rows, and
    // SenderId is a sender's label, and lending any of them would change what it means. The cost is a
    // repeated line, never a lost report.
    private static bool ReportCommitted(GatewaySession session, RunLease lease, string text)
        => lease.FailedRunId is { } run
            && session.GetHistorySnapshot().Any(row =>
                row.Role == MessageRole.Notification
                && row.AgentRunId == run
                && string.Equals(row.Content, text, StringComparison.Ordinal));

    // The call ids to name for outcome-unknown work, each once: two runs can reuse an id, and the id is all
    // the banner and the replacement's metadata carry.
    private static string[] DistinctCallIds(IReadOnlyList<RecoveredToolCall> calls)
        => [.. calls.Select(static call => call.ToolCallId).Distinct(StringComparer.Ordinal)];

    private SessionEntry BuildBannerEntry(ReplayAttempt replay, RecoveryCause cause)
    {
        var banner = cause == RecoveryCause.GatewayRestart
            ? BuildReplayBanner(replay.InitiatingContent, replay.Attempt, _options.MaxAutoReplayAttempts)
            : BuildRunFailureBanner(replay.InitiatingContent, replay.Attempt, _options.MaxAutoReplayAttempts);
        var outcomeUnknownToolCallIds = replay.OutcomeUnknownToolCallIds ?? [];
        if (outcomeUnknownToolCallIds.Length > 0)
        {
            banner += "\n\nOutcome is unknown for these recent tool calls; verify each before repeating it: "
                + string.Join(", ", outcomeUnknownToolCallIds);
        }

        return new SessionEntry
        {
            Role = MessageRole.System,
            Content = banner,
            Timestamp = DateTimeOffset.UtcNow,
            IsReplayBanner = true
        };
    }

    /// <summary>
    /// The status row for a LIVE run failure: the failure first, then either that a replacement is queued
    /// (queued, not started, because admission only enqueues it) or why none was, with the next step for the
    /// reader. A "queued" row is saved with the reservation, before the post, so it is only as true as that
    /// reservation; a refused post replaces it, by identity, with the not-queued row. It never mentions a
    /// restart, because nothing restarted.
    /// </summary>
    private static string BuildRunFailureNotice(string? failureDetail, ReplayAttempt replay, int maxAttempts)
    {
        var failure = failureDetail is null
            ? RunFailureNoticeLead + "."
            : $"{RunFailureNoticeLead}: {failureDetail}";
        var recovery = replay.Status switch
        {
            ReplayStatus.Queued =>
                $"Automatic recovery was queued (attempt {replay.Attempt} of {maxAttempts}).",
            ReplayStatus.AttemptsExhausted =>
                $"Automatic recovery was not queued: all {maxAttempts} automatic recovery attempts for this input are used. {ResumeHint}",
            ReplayStatus.NoInitiatingInput =>
                $"Automatic recovery was not queued: no initiating message could be identified to replay. {ResumeHint}",
            ReplayStatus.AdmissionRefused =>
                $"Automatic recovery was not queued: the gateway could not accept the replacement message right now. {ResumeHint}",
            _ =>
                $"Automatic recovery was not queued: it is not available for this session. {ResumeHint}"
        };

        return $"{failure} {recovery}";
    }

    /// <summary>
    /// Bounds a failed run's detail for a status row: one line, free of control characters, ending like a
    /// sentence so the next sentence reads cleanly, and never longer than <see cref="MaxFailureDetailLength"/>
    /// characters INCLUDING that ending. <see langword="null"/> when the run supplied no usable detail.
    /// </summary>
    private static string? FormatFailureDetail(string? detail)
    {
        // One character past the bound is enough to know the detail has to be cut.
        var text = detail.SanitizeExternalText(MaxFailureDetailLength + 1);
        if (text.Length == 0)
            return null;

        // Add the ending first and bound the finished sentence, so the period can never push it past the limit.
        var sentence = text[^1] is '.' or '!' or '?' ? text : text + ".";
        return sentence.Length <= MaxFailureDetailLength
            ? sentence
            : sentence.SafeTruncate(MaxFailureDetailLength - Ellipsis.Length, Ellipsis);
    }

    private async Task PublishPersistedNotificationAsync(
        GatewaySession session,
        AgentId agentId,
        SessionEntry notification,
        CancellationToken cancellationToken)
    {
        if (!session.ConversationId.IsInitialized())
        {
            _logger.LogWarning(
                "Persisted interrupted-turn notification for session {SessionId} has no conversation id; live event publication was skipped",
                session.SessionId.Value);
            return;
        }

        var bindings = System.Collections.Immutable.ImmutableArray<ConversationBindingSnapshot>.Empty;
        if (_conversations is not null)
        {
            try
            {
                var conversation = await _conversations.GetAsync(session.ConversationId, cancellationToken)
                    .ConfigureAwait(false);
                bindings = ConversationBindingSnapshot.FromMany(conversation?.ChannelBindings);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The committed transcript remains authoritative. A later client hydration or
                // reconciliation can recover it even when binding projection is temporarily unavailable.
                _logger.LogWarning(
                    ex,
                    "Failed to snapshot bindings for persisted interrupted-turn notification in conversation {ConversationId}; publishing with no bindings",
                    session.ConversationId.Value);
            }
        }

        try
        {
            var accepted = await _eventPublisher.PublishAsync(new ConversationSessionItemPersistedEvent
            {
                AgentId = agentId,
                ConversationId = session.ConversationId,
                SessionId = session.SessionId,
                Bindings = bindings,
                Item = notification,
                OccurredAt = notification.Timestamp
            }, cancellationToken).ConfigureAwait(false);

            if (!accepted)
            {
                _logger.LogWarning(
                    "Conversation event publisher rejected persisted interrupted-turn notification for session {SessionId}; the committed session item will not be retried",
                    session.SessionId.Value);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Publication is not transactional with session persistence. Never compensate the durable
            // mutation: consumers reconcile from the transcript when live best-effort delivery is missed.
            _logger.LogWarning(
                ex,
                "Failed to publish persisted interrupted-turn notification for session {SessionId}; the committed session item will not be retried",
                session.SessionId.Value);
        }
    }

    private async Task<bool> IsAgentOnlyConversationAsync(
        GatewaySession session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInteractive || _conversations is null || !session.ConversationId.IsInitialized())
            return false;

        try
        {
            var conversation = await _conversations.GetAsync(session.ConversationId, cancellationToken)
                .ConfigureAwait(false);
            return conversation is not null
                && conversation.Participants.Count > 0
                && conversation.Participants.All(static participant =>
                    participant.CitizenId.Kind != CitizenKind.User);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Failed to resolve conversation {ConversationId} participants during interrupted-turn scan; preserving notify-only behavior",
                session.ConversationId.Value);
            return false;
        }
    }

    private Task<ReplayAttempt> TryAutoReplayAsync(GatewaySession session, AgentId agentId, CancellationToken cancellationToken)
    {
        // Read existing replay count from metadata.
        var replayCount = 0;
        if (session.Metadata.TryGetValue(MetadataKeyReplayCount, out var raw))
        {
            replayCount = raw switch
            {
                int i => i,
                long l => (int)l,
                string s when int.TryParse(s, out var parsed) => parsed,
                _ => 0
            };
        }

        if (replayCount >= _options.MaxAutoReplayAttempts)
        {
            _logger.LogWarning(
                "Session {SessionId} has reached max auto-replay attempts ({Max}); falling back to notification only",
                session.SessionId.Value, _options.MaxAutoReplayAttempts);
            return Task.FromResult(new ReplayAttempt(ReplayStatus.AttemptsExhausted, replayCount));
        }

        // Human requests are authoritative user entries. The inbound path never stores a sender label on a
        // row, so a user row that carries one was appended or injected by a caller and is not what a run was
        // started with. Agent-origin input has no such shape: it is recovered only through the run lease the
        // gateway wrote itself, and a sentinel without one has no input to name.
        var initiatingEntry = session.History
            .Where(e => !e.IsCrashSentinel
                && !string.IsNullOrWhiteSpace(e.Content)
                && e.Role == MessageRole.User
                && string.IsNullOrWhiteSpace(e.SenderId))
            .OrderBy(e => e.Timestamp)
            .LastOrDefault();

        if (initiatingEntry is null)
        {
            _logger.LogWarning(
                "Session {SessionId} has no authoritative initiating message; recovery remains retryable",
                session.SessionId.Value);
            return Task.FromResult(new ReplayAttempt(ReplayStatus.NoInitiatingInput));
        }

        var outcomeUnknownToolCallIds = DistinctCallIds(RecoveryEvidence.Collect(session.GetHistorySnapshot()).OutcomeUnknown);
        var replay = BuildReplacementMessage(session, agentId, initiatingEntry, outcomeUnknownToolCallIds, recovery: null);

        var accepted = _orchestrator!.Post(replay);

        _logger.LogInformation(
            "Auto-replay for session {SessionId}: Post returned {Accepted} (attempt {Attempt}/{Max}, outcome-unknown tools {OutcomeUnknownCount})",
            session.SessionId.Value, accepted, replayCount + 1, _options.MaxAutoReplayAttempts,
            outcomeUnknownToolCallIds.Length);

        if (!accepted)
            return Task.FromResult(new ReplayAttempt(ReplayStatus.AdmissionRefused));

        session.Metadata[MetadataKeyReplayCount] = replayCount + 1;
        return Task.FromResult(new ReplayAttempt(
            ReplayStatus.Queued, replayCount + 1, initiatingEntry.Content, outcomeUnknownToolCallIds));
    }
}
