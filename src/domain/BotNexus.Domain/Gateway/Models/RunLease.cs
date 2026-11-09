using BotNexus.Domain.Primitives;
using BotNexus.Domain.Text;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// The gateway's durable claim that it admitted a run for one stored input. It rides on the crash
/// sentinel the host writes before every run, so it is created, kept and removed together with that
/// sentinel and survives anything the sentinel survives, including a restart.
/// </summary>
/// <remarks>
/// <para>
/// Recovery uses a lease instead of looking at the transcript. A transcript row says what was written, not
/// who admitted it: a caller-labelled message, a mid-run steer or an appended note all look like an input.
/// A lease is written only by the code that admitted the run, so the input it names is the input the
/// gateway actually ran, whatever route, channel or role it arrived through.
/// </para>
/// <para>
/// The lease is data, not authority over the transcript. It never changes what a run sees and it is not
/// shown to the model; it only lets recovery decide, from durable state, whether an input is owed a
/// replacement, how many it has had, and which failed run to inspect first.
/// </para>
/// </remarks>
public sealed record RunLease
{
    /// <summary>The longest failure detail a lease keeps, in characters.</summary>
    public const int MaxFailureDetailLength = 512;

    private const string Ellipsis = "...";

    /// <summary>This lease's identity. Recovery consumes a lease by id, so a repeated signal cannot act twice.</summary>
    public required RunLeaseId LeaseId { get; init; }

    /// <summary>Where the lease is in its life.</summary>
    public required RunLeaseState State { get; init; }

    /// <summary>
    /// How many automatic replacements precede this run: 0 for the run an input was admitted for, n for the
    /// n-th accepted replacement. The recovery budget is counted here, per origin, so one input's failures
    /// never spend another input's attempts.
    /// </summary>
    public int Attempt { get; init; }

    /// <summary>
    /// The input this run was started for. While the lease is <see cref="RunLeaseState.Pending"/> the
    /// replacement's own entry does not exist yet, so this is the origin the replacement will re-send.
    /// </summary>
    public required InputRef Input { get; init; }

    /// <summary>
    /// The input the chain of runs started from: equal to <see cref="Input"/> for an original run and
    /// copied unchanged onto every replacement, so a replacement of a replacement still resolves to what
    /// the user or caller actually sent.
    /// </summary>
    public required InputRef Origin { get; init; }

    /// <summary>The lease this one replaced when recovery re-admitted its input; null for an original run.</summary>
    public RunLeaseId? InterruptedLeaseId { get; init; }

    /// <summary>When the run settled as failed; null until it does.</summary>
    public DateTimeOffset? FailedAt { get; init; }

    /// <summary>
    /// What the run reported when it failed, bounded to <see cref="MaxFailureDetailLength"/> characters on a
    /// single line; null while the run is in flight.
    /// </summary>
    public string? FailureDetail { get; init; }

    /// <summary>
    /// The execution identity of the run that failed. Tool rows carrying it are the evidence of what that
    /// run completed; null until the run fails or when it never reported one.
    /// </summary>
    public AgentRunId? FailedRunId { get; init; }

    /// <summary>
    /// While <see cref="RunLeaseState.Pending"/>, an opaque token naming the gateway instance that queued the
    /// replacement this lease reserves; null in every other state. A token that is not the current
    /// instance's belongs to a process that no longer exists, so the replacement it queued is gone and
    /// recovery must queue it again. This is what lets a startup scan in the same process leave a
    /// replacement that is merely waiting its turn alone.
    /// </summary>
    public string? QueuedBy { get; init; }

    /// <summary>Creates the lease for a run about to start for <paramref name="input"/>.</summary>
    /// <param name="input">The stored input the run is being admitted for.</param>
    /// <returns>A running lease on attempt 0 whose origin is its own input.</returns>
    public static RunLease Admit(InputRef input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new RunLease
        {
            LeaseId = RunLeaseId.Create(),
            State = RunLeaseState.Running,
            Attempt = 0,
            Input = input,
            Origin = input
        };
    }

    /// <summary>
    /// Reserves the replacement for this lease's input. The result is the lease that takes this one's
    /// place: a new identity on the next attempt, <see cref="RunLeaseState.Pending"/> until the
    /// replacement adopts it, with the origin carried over unchanged so a replacement of a replacement
    /// still resolves to what was originally sent. This lease is superseded, not mutated, so a repeated
    /// signal that still carries its identity finds nothing to act on.
    /// </summary>
    /// <param name="queuedBy">The token of the gateway instance that is about to queue the replacement.</param>
    /// <returns>The pending successor, on attempt <see cref="Attempt"/> + 1.</returns>
    public RunLease QueueSuccessor(string queuedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queuedBy);
        return new RunLease
        {
            LeaseId = RunLeaseId.Create(),
            State = RunLeaseState.Pending,
            Attempt = Attempt + 1,
            Input = Origin,
            Origin = Origin,
            InterruptedLeaseId = LeaseId,
            QueuedBy = queuedBy
        };
    }

    /// <summary>
    /// Hands a pending reservation to a gateway instance that is about to queue its replacement again, for
    /// a reservation whose queuing instance is gone. The attempt is unchanged: re-queuing a lost replacement
    /// never costs budget.
    /// </summary>
    /// <param name="queuedBy">The token of the instance that is re-queuing the replacement.</param>
    /// <returns>This lease reserved for <paramref name="queuedBy"/>, or this lease itself when it is not pending.</returns>
    public RunLease Requeue(string queuedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queuedBy);
        return State != RunLeaseState.Pending ? this : this with { QueuedBy = queuedBy };
    }

    /// <summary>
    /// Turns the reservation into the lease of the replacement run that is starting. Exactly one run can do
    /// this: only a pending lease adopts, so a second replacement for the same reservation finds it taken.
    /// </summary>
    /// <param name="input">The replacement's own stored inbound entry.</param>
    /// <returns>The running lease, or this lease itself when it was not pending.</returns>
    public RunLease Adopt(InputRef input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return State != RunLeaseState.Pending
            ? this
            : this with { State = RunLeaseState.Running, Input = input, QueuedBy = null };
    }

    /// <summary>
    /// Records that the run settled as failed. Idempotent: a lease that is not running is returned
    /// unchanged, so a repeated signal cannot overwrite what the first one recorded.
    /// </summary>
    /// <param name="failedAt">When the failure was recorded.</param>
    /// <param name="detail">What the run reported; sanitized to one line and bounded here, so no caller can store more.</param>
    /// <param name="failedRunId">The failed run's execution identity, when it reported one.</param>
    /// <returns>The failed lease, or this lease when it was not running.</returns>
    public RunLease MarkFailed(DateTimeOffset failedAt, string? detail, AgentRunId? failedRunId)
        => State != RunLeaseState.Running
            ? this
            : this with
            {
                State = RunLeaseState.Failed,
                FailedAt = failedAt,
                FailureDetail = BoundDetail(detail),
                FailedRunId = failedRunId
            };

    private static string? BoundDetail(string? detail)
    {
        var text = detail.SanitizeExternalText(MaxFailureDetailLength + 1);
        if (text.Length > MaxFailureDetailLength)
            text = text.SafeTruncate(MaxFailureDetailLength - Ellipsis.Length, Ellipsis) ?? string.Empty;

        return text.Length == 0 ? null : text;
    }
}
