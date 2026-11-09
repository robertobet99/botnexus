using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Streaming;

/// <summary>
/// The one definition of a terminal run failure, shared by the streaming finalizer and the blocking
/// branch of <see cref="GatewayHost"/> so both leave the same evidence behind: a single error row, the
/// crash sentinel kept as recovery's durable marker, and one
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
    /// Publishes a lifecycle event for <paramref name="session"/> only when the run's final save
    /// persisted. A save that was skipped (the session was deleted, reset or rebound mid-run) or that
    /// never completed means the evidence a subscriber would act on is not durable, so nothing is announced.
    /// </summary>
    internal static async Task PublishAfterSaveAsync(
        SessionLifecycleEvents? lifecycleEvents,
        GatewaySession session,
        SessionLifecycleEventType type,
        SessionSaveOutcome? saveOutcome,
        CancellationToken cancellationToken)
    {
        if (lifecycleEvents is null || saveOutcome != SessionSaveOutcome.Persisted)
            return;

        await lifecycleEvents.PublishAsync(
            new SessionLifecycleEvent(session.SessionId.Value, session.AgentId.Value, type, session),
            cancellationToken);
    }
}
