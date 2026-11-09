using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Streaming;

/// <summary>
/// The one definition of a terminal run failure, shared by the streaming finalizer and the blocking
/// branch of <see cref="GatewayHost"/> so both leave the same evidence behind: a single error row, the
/// crash sentinel kept as recovery's durable marker with the run's lease recorded as failed, and one
/// <see cref="SessionLifecycleEventType.TerminalFailure"/> published only after the fenced final save
/// persisted.
/// </summary>
/// <remarks>
/// A failure is a settled run whose completion status is <c>Failed</c>. That covers both ways the core
/// ends a failed run (an error message it returns, and an exception it converts). Cancellation and
/// infrastructure exceptions are deliberately outside this type: they never reach a settled completion,
/// so they publish nothing, write no error row here and leave the sentinel exactly as they always did.
/// </remarks>
internal static class TerminalRunOutcome
{
    private const string NoDetail = "The agent run failed but supplied no detail.";
    private const string FailedStatus = "Failed";
    private const string ErrorRowPrefix = "Agent stream error: ";

    /// <summary>True when the settled run's completion says it failed. A missing completion is not a failure.</summary>
    internal static bool IsFailure(RunCompletionSignal? completion)
        => string.Equals(completion?.Status, FailedStatus, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The text that explains a failed run. The terminal turn's own error wins, then the completion's
    /// detail, the same order the streaming boundary emits them in, so both branches record the same words.
    /// </summary>
    internal static string ResolveDetail(string? terminalError, RunCompletionSignal? completion)
    {
        if (!string.IsNullOrWhiteSpace(terminalError))
            return terminalError;

        var detail = completion?.Detail;
        return string.IsNullOrWhiteSpace(detail) ? NoDetail : detail;
    }

    /// <summary>The single history row that records a failed run.</summary>
    internal static SessionEntry CreateErrorEntry(AgentRunId? agentRunId, string detail)
        => new()
        {
            AgentRunId = agentRunId,
            Role = MessageRole.System,
            Content = ErrorRowPrefix + detail
        };

    /// <summary>
    /// Records on the run's own lease that the run failed, so the one fenced final save that records the
    /// error row also records what recovery needs: which lease failed, when, why, and which run's tool rows
    /// to inspect. Only the lease the run was admitted under is touched, never "the sentinel" or "the
    /// latest lease", so a follow-up's or a later run's lease cannot be marked by mistake.
    /// </summary>
    /// <param name="session">The session whose sentinel carries the lease.</param>
    /// <param name="leaseId">The lease the run was admitted under, or null when the run was started without one.</param>
    /// <param name="detail">The failure detail the error row carries; the lease bounds it itself.</param>
    /// <param name="failedRunId">The failed run's execution identity, when it reported one.</param>
    /// <returns>
    /// <see langword="true"/> when the lease was found and recorded as failed; <see langword="false"/> when the
    /// run had no lease or its sentinel is gone.
    /// </returns>
    internal static bool MarkLeaseFailed(GatewaySession session, RunLeaseId? leaseId, string detail, AgentRunId? failedRunId)
        => leaseId is { } id
            && session.TryUpdateRunLease(id, lease => lease.MarkFailed(DateTimeOffset.UtcNow, detail, failedRunId));

    /// <summary>
    /// Publishes a lifecycle event for <paramref name="session"/> only when the run's final save
    /// persisted. A save that was skipped (the session was deleted, reset or rebound mid-run) or that
    /// never completed means the evidence a subscriber would act on is not durable, so nothing is announced.
    /// A failure carries the lease of the run that failed, fixed here at publication, so a subscriber that
    /// handles it later acts on that run's lease and cannot be pointed at a later run's.
    /// </summary>
    internal static async Task PublishAfterSaveAsync(
        SessionLifecycleEvents? lifecycleEvents,
        GatewaySession session,
        SessionLifecycleEventType type,
        SessionSaveOutcome? saveOutcome,
        CancellationToken cancellationToken,
        RunLeaseId? failedLeaseId = null)
    {
        if (lifecycleEvents is null || saveOutcome != SessionSaveOutcome.Persisted)
            return;

        await lifecycleEvents.PublishAsync(
            new SessionLifecycleEvent(session.SessionId.Value, session.AgentId.Value, type, session, failedLeaseId),
            cancellationToken);
    }
}
