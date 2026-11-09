using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// What the gateway's recovery service attaches to the replacement it queues for an interrupted run: the
/// reservation the replacement takes over, and what the interrupted work is known to have done, so the
/// model can be told to verify before it repeats anything.
/// </summary>
/// <remarks>
/// <para>
/// It is set only by the recovery service, never by a channel or a request, and the host does not trust it:
/// the replacement runs as a replacement only if a pending lease with <see cref="SuccessorLeaseId"/> is
/// still on the session, and only one replacement can take it. A message that names a lease that is gone,
/// taken or never existed is dropped before it writes anything or reaches the model.
/// </para>
/// <para>
/// The evidence is for the model only. It is rendered into the provider-bound message, ahead of the
/// original input, and is never written to the transcript, which keeps exactly the words that were sent.
/// </para>
/// </remarks>
public sealed record RecoveryContext
{
    /// <summary>The pending lease the replacement takes over when its turn starts.</summary>
    public required RunLeaseId SuccessorLeaseId { get; init; }

    /// <summary>Which replacement this is: 1 for the first, up to <see cref="MaxAttempts"/>.</summary>
    public required int Attempt { get; init; }

    /// <summary>How many replacements one input is allowed in total.</summary>
    public required int MaxAttempts { get; init; }

    /// <summary>
    /// Tool calls that finished with a successful result since the input was first sent, whatever attempt
    /// ran them. Their effects are already applied and must not be repeated.
    /// </summary>
    public IReadOnlyList<RecoveredToolCall> CompletedCalls { get; init; } = [];

    /// <summary>
    /// Tool calls since the input was first sent that did not finish with a successful result: no result
    /// was recorded, or it was an error, or it was incomplete. Whether their effects applied is unknown
    /// (a tool can fail after it has changed something), so each must be inspected before it is considered
    /// for repetition.
    /// </summary>
    public IReadOnlyList<RecoveredToolCall> OutcomeUnknownCalls { get; init; } = [];
}

/// <summary>One tool call the interrupted work made, as recovery names it to the model.</summary>
/// <param name="ToolCallId">The provider's id for the call. Two runs can reuse one, so it does not identify a call on its own: calls that share it are told apart by their run in the transcript and, to the model, by their arguments.</param>
/// <param name="ToolName">The tool that was called.</param>
/// <param name="Arguments">The call's arguments as stored (already redacted), bounded to a short preview; null when none were recorded.</param>
public sealed record RecoveredToolCall(string ToolCallId, string ToolName, string? Arguments);
