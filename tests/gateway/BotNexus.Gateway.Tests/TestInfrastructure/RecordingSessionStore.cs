using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Gateway.Tests.TestInfrastructure;

/// <summary>One fenced save as the store saw it: when it finished, how, and what it was asked to persist.</summary>
/// <param name="Sequence">Position on the harness clock at which the save completed or failed.</param>
/// <param name="SessionId">The session being saved.</param>
/// <param name="Outcome">The store's verdict, or null when the save threw.</param>
/// <param name="Failure">The exception the save threw, when it did.</param>
/// <param name="HistoryAtSave">A copy of the history the caller asked the store to persist.</param>
/// <param name="RunCompletionAtSave">The run completion the caller asked the store to persist.</param>
internal sealed record RecordedFencedSave(
    int Sequence,
    SessionId SessionId,
    SessionSaveOutcome? Outcome,
    Exception? Failure,
    IReadOnlyList<SessionEntry> HistoryAtSave,
    RunCompletionSignal? RunCompletionAtSave)
{
    /// <summary>True when the store reported the save as persisted.</summary>
    internal bool Persisted => Outcome == SessionSaveOutcome.Persisted;

    /// <summary>True when the transcript the caller asked to persist carried a crash sentinel.</summary>
    internal bool HadSentinel => HistoryAtSave.Any(entry => entry.IsCrashSentinel);
}

/// <summary>
/// Wraps the real session store to record every completed fenced save on the harness clock, which is how a
/// test proves a lifecycle event was published only after the evidence it announces became durable. The
/// in-memory store hands out its live aggregate, so a snapshot taken when an event is published cannot show
/// that; a save that was actually asked to persist, and the order it finished in, can.
/// </summary>
/// <remarks>
/// <para>
/// Every member of the interface is implemented here and forwards to the wrapped store, including the ones
/// the interface gives a default body. A member left to its default would run that default against this
/// wrapper instead of the wrapped store's own implementation, silently changing what the store does; the
/// harness self-tests pin that every member is forwarded.
/// </para>
/// <para>
/// A test can make the next fenced save fail the two ways the host must survive: refused
/// (<see cref="RejectNextFencedSave"/>, as when the session was deleted or reset mid-run) or throwing
/// (<see cref="FailNextFencedSave"/>). Either way nothing is persisted.
/// </para>
/// </remarks>
internal sealed class RecordingSessionStore(ISessionStore inner, HarnessClock clock) : ISessionStore
{
    private readonly object _sync = new();
    private readonly List<RecordedFencedSave> _fencedSaves = [];
    private Fault _nextFencedFault;

    private enum Fault
    {
        None,
        Reject,
        Throw
    }

    /// <summary>Every fenced save that has finished, in completion order.</summary>
    internal IReadOnlyList<RecordedFencedSave> FencedSaves
    {
        get { lock (_sync) return [.. _fencedSaves]; }
    }

    /// <summary>Makes the next fenced save return a rebound outcome without persisting anything.</summary>
    internal void RejectNextFencedSave() => Arm(Fault.Reject);

    /// <summary>Makes the next fenced save throw without persisting anything.</summary>
    internal void FailNextFencedSave() => Arm(Fault.Throw);

    async Task<SessionSaveOutcome> ISessionStore.SaveAsync(
        GatewaySession session,
        SessionWriteFence fence,
        CancellationToken cancellationToken)
    {
        // What the caller asks the store to persist, captured before the store can change anything.
        var requestedHistory = session.GetHistorySnapshot();
        var requestedCompletion = session.RunCompletion;
        var fault = TakeFault();
        try
        {
            SessionSaveOutcome outcome;
            switch (fault)
            {
                case Fault.Reject:
                    outcome = SessionSaveOutcome.Rebound;
                    break;
                case Fault.Throw:
                    throw new IOException("Synthetic session store failure.");
                default:
                    outcome = await inner.SaveAsync(session, fence, cancellationToken).ConfigureAwait(false);
                    break;
            }

            Record(session.SessionId, outcome, failure: null, requestedHistory, requestedCompletion);
            return outcome;
        }
        catch (Exception ex)
        {
            Record(session.SessionId, outcome: null, ex, requestedHistory, requestedCompletion);
            throw;
        }
    }

    Task<SessionHistoryScanPage> ISessionStore.ScanHistoryAsync(
        SessionId sessionId, SessionHistoryScan scan, CancellationToken cancellationToken)
        => inner.ScanHistoryAsync(sessionId, scan, cancellationToken);

    Task<GatewaySession?> ISessionStore.GetAsync(SessionId sessionId, CancellationToken cancellationToken)
        => inner.GetAsync(sessionId, cancellationToken);

    Task<GatewaySession> ISessionStore.GetOrCreateAsync(
        SessionId sessionId, AgentId agentId, CancellationToken cancellationToken)
        => inner.GetOrCreateAsync(sessionId, agentId, cancellationToken);

    Task ISessionStore.SaveAsync(GatewaySession session, CancellationToken cancellationToken)
        => inner.SaveAsync(session, cancellationToken);

    Task<int> ISessionStore.RebindSessionsAsync(
        AgentId agentId, string sessionIdPrefix, ConversationId targetConversationId, CancellationToken cancellationToken)
        => inner.RebindSessionsAsync(agentId, sessionIdPrefix, targetConversationId, cancellationToken);

    Task<SessionAppendMutationResult> ISessionStore.AppendEntriesAsync(
        SessionId sessionId, IReadOnlyList<SessionEntry> entries, CancellationToken cancellationToken)
        => inner.AppendEntriesAsync(sessionId, entries, cancellationToken);

    Task<SessionMetadataMutationResult> ISessionStore.PatchMetadataAsync(
        SessionId sessionId, IReadOnlyDictionary<string, object?> patch, CancellationToken cancellationToken)
        => inner.PatchMetadataAsync(sessionId, patch, cancellationToken);

    Task<SessionStatusMutationResult> ISessionStore.TransitionStatusAsync(
        SessionId sessionId,
        IReadOnlyList<SessionStatus> expectedStatuses,
        SessionStatus newStatus,
        CancellationToken cancellationToken)
        => inner.TransitionStatusAsync(sessionId, expectedStatuses, newStatus, cancellationToken);

    Task<UnresolvedCrashSentinelPage> ISessionStore.ListUnresolvedCrashSentinelsAsync(
        int limit, string? cursor, CancellationToken cancellationToken)
        => inner.ListUnresolvedCrashSentinelsAsync(limit, cursor, cancellationToken);

    Task<SessionCleanupPlanPage> ISessionStore.ListCleanupPlanAsync(
        int limit, string? cursor, CancellationToken cancellationToken)
        => inner.ListCleanupPlanAsync(limit, cursor, cancellationToken);

    Task<SessionCleanupPlanPage> ISessionStore.ListCleanupPlanAsync(
        int limit, bool includeBytes, string? cursor, CancellationToken cancellationToken)
        => inner.ListCleanupPlanAsync(limit, includeBytes, cursor, cancellationToken);

    Task<SessionMutationOutcome> ISessionStore.ExpireIfMatchesAsync(
        SessionCleanupFence fence, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        => inner.ExpireIfMatchesAsync(fence, expiresAt, cancellationToken);

    Task<SessionMutationOutcome> ISessionStore.DeleteIfMatchesAsync(
        SessionCleanupFence fence, CancellationToken cancellationToken)
        => inner.DeleteIfMatchesAsync(fence, cancellationToken);

    Task ISessionStore.DeleteAsync(SessionId sessionId, CancellationToken cancellationToken)
        => inner.DeleteAsync(sessionId, cancellationToken);

    Task ISessionStore.ArchiveAsync(SessionId sessionId, CancellationToken cancellationToken)
        => inner.ArchiveAsync(sessionId, cancellationToken);

    Task<IReadOnlyList<GatewaySession>> ISessionStore.ListAsync(AgentId? agentId, CancellationToken cancellationToken)
        => inner.ListAsync(agentId, cancellationToken);

    Task<IReadOnlyList<SessionSummary>> ISessionStore.ListSummariesAsync(
        DateTimeOffset updatedAfter, int? limit, int offset, CancellationToken cancellationToken)
        => inner.ListSummariesAsync(updatedAfter, limit, offset, cancellationToken);

    Task<SessionSummaryPage> ISessionStore.ListSummaryPageAsync(
        SessionSummaryQuery query, CancellationToken cancellationToken)
        => inner.ListSummaryPageAsync(query, cancellationToken);

    Task<IReadOnlyList<GatewaySession>> ISessionStore.ListByChannelAsync(
        AgentId agentId, ChannelKey channelType, CancellationToken cancellationToken)
        => inner.ListByChannelAsync(agentId, channelType, cancellationToken);

    Task<IReadOnlyList<GatewaySession>> ISessionStore.ListByConversationAsync(
        ConversationId conversationId, AgentId? agentId, CancellationToken cancellationToken)
        => inner.ListByConversationAsync(conversationId, agentId, cancellationToken);

    Task<IReadOnlyList<GatewaySession>> ISessionStore.GetExistenceAsync(
        AgentId agentId, ExistenceQuery query, CancellationToken cancellationToken)
        => inner.GetExistenceAsync(agentId, query, cancellationToken);

    Task ISessionStore.SaveSubAgentSessionAsync(SubAgentInfo info, CancellationToken cancellationToken)
        => inner.SaveSubAgentSessionAsync(info, cancellationToken);

    Task ISessionStore.UpdateSubAgentSessionAsync(SubAgentInfo info, CancellationToken cancellationToken)
        => inner.UpdateSubAgentSessionAsync(info, cancellationToken);

    Task<IReadOnlyList<SubAgentRunDetail>> ISessionStore.ListSubAgentSessionsAsync(
        SessionId sessionId, CancellationToken cancellationToken)
        => inner.ListSubAgentSessionsAsync(sessionId, cancellationToken);

    Task<IReadOnlyList<SubAgentRunDetail>> ISessionStore.ListAllSubAgentSessionsAsync(
        string? status,
        int limit,
        CancellationToken cancellationToken,
        string? parentSessionId,
        string? childAgentId,
        int offset)
        => inner.ListAllSubAgentSessionsAsync(status, limit, cancellationToken, parentSessionId, childAgentId, offset);

    Task<SubAgentRunDetail?> ISessionStore.FindSubAgentSpawnAsync(
        SessionId parentSessionId, string toolCallId, CancellationToken cancellationToken)
        => inner.FindSubAgentSpawnAsync(parentSessionId, toolCallId, cancellationToken);

    Task<string?> ISessionStore.GetSubAgentResultSummaryAsync(string subAgentId, CancellationToken cancellationToken)
        => inner.GetSubAgentResultSummaryAsync(subAgentId, cancellationToken);

    Task<SubAgentRunDetail?> ISessionStore.GetSubAgentSessionAsync(string subAgentId, CancellationToken cancellationToken)
        => inner.GetSubAgentSessionAsync(subAgentId, cancellationToken);

    Task<string?> ISessionStore.ConsumeSubAgentResultAsync(
        string subAgentId,
        SessionId parentSessionId,
        ConversationId? parentConversationId,
        SessionEntry result,
        CancellationToken cancellationToken)
        => inner.ConsumeSubAgentResultAsync(subAgentId, parentSessionId, parentConversationId, result, cancellationToken);

    Task<SessionStats?> ISessionStore.GetStatsAsync(AgentId? agentId, CancellationToken cancellationToken)
        => inner.GetStatsAsync(agentId, cancellationToken);

    private void Arm(Fault fault)
    {
        lock (_sync)
            _nextFencedFault = fault;
    }

    private Fault TakeFault()
    {
        lock (_sync)
        {
            var fault = _nextFencedFault;
            _nextFencedFault = Fault.None;
            return fault;
        }
    }

    private void Record(
        SessionId sessionId,
        SessionSaveOutcome? outcome,
        Exception? failure,
        IReadOnlyList<SessionEntry> history,
        RunCompletionSignal? completion)
    {
        var sequence = clock.Next();
        lock (_sync)
            _fencedSaves.Add(new RecordedFencedSave(sequence, sessionId, outcome, failure, history, completion));
    }
}
