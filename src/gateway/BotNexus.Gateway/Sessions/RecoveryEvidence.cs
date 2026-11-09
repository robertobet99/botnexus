using BotNexus.Domain.Primitives;
using BotNexus.Domain.Text;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Audit;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Reads the transcript to say what interrupted work is known to have done: which tool calls finished with
/// a successful result and which did not (no result, an errored one, or an incomplete one). Recovery uses it
/// to tell the model what not to repeat and what to check first; the gateway itself never re-runs a tool.
/// </summary>
/// <remarks>
/// A call is identified by the run that made it AND its provider call id, never by the call id alone.
/// Providers can reuse an id in a later run (a replacement's tool call can be "call-1" again), and merging
/// the two by id would let the later run's success hide the earlier run's call whose outcome is unknown, and
/// would mix their arguments. This is the run-scoped identity the audit sink already gives these rows.
/// <para>
/// A row that names no run cannot be tied to one, so it is never paired with a row that does, and two such
/// rows are paired only inside one uninterrupted stretch of tool rows: every run begins with its own inbound
/// row, so no such stretch spans two runs. Whatever cannot be paired stays distinct and fails safe: a start
/// with no pair is outcome-unknown, and a result is judged on its own.
/// </para>
/// </remarks>
internal static class RecoveryEvidence
{
    /// <summary>The most calls named in either list, newest ones kept for completed work.</summary>
    internal const int MaxCallsPerList = 20;

    private const int ArgumentsPreviewLength = 160;
    private const string Ellipsis = "...";

    /// <summary>
    /// Collects the tool calls made after <paramref name="origin"/>, the input the work started from. Every
    /// attempt since that input is in the window, so a replacement is told about effects applied by earlier
    /// replacements too, not only by the run that failed last.
    /// </summary>
    /// <param name="history">The session history, in transcript order.</param>
    /// <param name="origin">The stored input the chain of runs started from, an instance of <paramref name="history"/>.</param>
    /// <returns>The calls that finished successfully and the calls whose outcome is unknown, each bounded.</returns>
    internal static (IReadOnlyList<RecoveredToolCall> Completed, IReadOnlyList<RecoveredToolCall> OutcomeUnknown) Collect(
        IReadOnlyList<SessionEntry> history,
        SessionEntry origin)
    {
        var originIndex = IndexOf(history, origin);
        return Collect(originIndex < 0 ? history : [.. history.Skip(originIndex + 1)]);
    }

    /// <summary>
    /// Collects tool calls from a whole history. This is the scan the startup path has always used for
    /// sentinels that carry no lease, so no input is known to bound the window.
    /// </summary>
    /// <param name="history">The session history, in transcript order.</param>
    /// <returns>The calls that finished successfully and the calls whose outcome is unknown, each bounded.</returns>
    internal static (IReadOnlyList<RecoveredToolCall> Completed, IReadOnlyList<RecoveredToolCall> OutcomeUnknown) Collect(
        IEnumerable<SessionEntry> history)
    {
        var byIdentity = new Dictionary<CallIdentity, TrackedCall>();
        var order = new List<TrackedCall>();
        var stretch = 0;

        foreach (var entry in history)
        {
            if (!entry.Role.Equals(MessageRole.Tool))
            {
                // Any other row ends the stretch of tool rows that rows without a run are paired within.
                stretch++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.ToolCallId))
                continue;

            var identity = CallIdentity.Of(entry.AgentRunId, stretch, entry.ToolCallId);
            if (!byIdentity.TryGetValue(identity, out var call))
            {
                call = new TrackedCall(entry.ToolCallId);
                byIdentity.Add(identity, call);
                order.Add(call);
            }

            // Within one identity the first start is the call as issued and the last result is its outcome.
            if (entry.IsToolStartRow())
                call.Start ??= entry;
            else
                call.Result = entry;
        }

        var completed = new List<RecoveredToolCall>();
        var unknown = new List<RecoveredToolCall>();
        var told = new HashSet<(bool Finished, RecoveredToolCall Call)>();
        foreach (var call in order)
        {
            var recovered = new RecoveredToolCall(
                call.ToolCallId,
                NameOf(call.Result) ?? NameOf(call.Start) ?? "unknown",
                PreviewArguments(ArgumentsOf(call.Start, call.Result)));

            // Two calls that read exactly alike (an unpaired start and its synthesized result, say) are
            // told once. Calls that differ, whatever they share, are each told.
            var finished = call.Result is not null && IsSuccessfulReceipt(call.Result);
            if (!told.Add((finished, recovered)))
                continue;

            (finished ? completed : unknown).Add(recovered);
        }

        return (
            completed.Count > MaxCallsPerList ? completed.GetRange(completed.Count - MaxCallsPerList, MaxCallsPerList) : completed,
            unknown.Count > MaxCallsPerList ? unknown.GetRange(0, MaxCallsPerList) : unknown);
    }

    /// <summary>
    /// True when a tool result row proves its call finished: the tool returned, and what it returned was
    /// neither an error nor incomplete. Only such a receipt is reported as work not to repeat. An errored
    /// result does not show the call had no effect, since a tool can fail after it has changed something,
    /// and a synthesized or incomplete result is the gateway recording that nothing was observed, not that
    /// the tool ran. Those calls are outcome-unknown: to be verified, never assumed done or not done.
    /// </summary>
    /// <param name="result">A tool result row.</param>
    /// <returns><see langword="true"/> when the row is a successful receipt.</returns>
    internal static bool IsSuccessfulReceipt(SessionEntry result)
        => !result.ToolIsError && !result.ToolIsIncomplete;

    // The start row records the call as it was issued. A result row repeats those arguments only when the
    // code that wrote it had them in hand; otherwise it carries the audit sink's "no arguments" placeholder,
    // which says nothing about what the call was given. So the placeholder never hides arguments that the
    // other row has, and a call that really took none still reads as "{}".
    private static string? ArgumentsOf(SessionEntry? start, SessionEntry? result)
    {
        var fromStart = start?.ToolArgs;
        var fromResult = result?.ToolArgs;
        if (IsInformative(fromStart))
            return fromStart;

        return IsInformative(fromResult) ? fromResult : fromStart ?? fromResult;
    }

    private static bool IsInformative(string? arguments)
        => !string.IsNullOrWhiteSpace(arguments)
            && !string.Equals(arguments.Trim(), DefaultToolAuditSink.NoArguments, StringComparison.Ordinal);

    private static string? NameOf(SessionEntry? entry)
        => entry is null
            ? null
            : !string.IsNullOrWhiteSpace(entry.ToolName)
                ? entry.ToolName
                : entry.IsToolStartRow() && !string.IsNullOrWhiteSpace(entry.Content) ? entry.Content : null;

    private static string? PreviewArguments(string? arguments)
    {
        var text = arguments.SanitizeExternalText(ArgumentsPreviewLength + 1);
        if (text.Length == 0)
            return null;

        return text.Length <= ArgumentsPreviewLength
            ? text
            : text.SafeTruncate(ArgumentsPreviewLength - Ellipsis.Length, Ellipsis);
    }

    private static int IndexOf(IReadOnlyList<SessionEntry> history, SessionEntry entry)
    {
        for (var index = 0; index < history.Count; index++)
        {
            if (ReferenceEquals(history[index], entry))
                return index;
        }

        return -1;
    }

    // Who a call belongs to. A row with a run is that run's call. A row without one belongs to no run, so it
    // is placed in the stretch of tool rows it sits in, which keeps it apart from every row of a run and from
    // rows without a run in any other stretch.
    private readonly record struct CallIdentity(string? RunId, int Stretch, string ToolCallId)
    {
        internal static CallIdentity Of(AgentRunId? run, int stretch, string toolCallId)
            => run is { } named
                ? new CallIdentity(named.Value, 0, toolCallId)
                : new CallIdentity(null, stretch, toolCallId);
    }

    private sealed class TrackedCall(string toolCallId)
    {
        internal string ToolCallId { get; } = toolCallId;

        internal SessionEntry? Start { get; set; }

        internal SessionEntry? Result { get; set; }
    }
}
