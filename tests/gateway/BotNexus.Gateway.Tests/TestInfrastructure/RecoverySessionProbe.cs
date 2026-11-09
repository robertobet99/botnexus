using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Tests.TestInfrastructure;

/// <summary>
/// Reads a session transcript the way a history reader would, classifying rows by what they are
/// (role and typed flags) rather than by wording, so assertions stay independent of notice text.
/// </summary>
internal static class RecoverySessionProbe
{
    /// <summary>
    /// Rows that record a failed run: system-role transcript rows other than the crash sentinel, a
    /// replay banner or a compaction summary. This is the row the streaming path already writes
    /// ("the run failed, and why"); the blocking path must write the same single row.
    /// </summary>
    internal static IReadOnlyList<SessionEntry> ErrorRows(this IEnumerable<SessionEntry> history)
        =>
        [
            .. history.Where(entry => entry.Role.Equals(MessageRole.System)
                && !entry.IsCrashSentinel
                && !entry.IsReplayBanner
                && !entry.IsCompactionSummary)
        ];

    /// <summary>Gateway-authored status rows (the notification role, never forwarded to the model).</summary>
    internal static IReadOnlyList<SessionEntry> StatusRows(this IEnumerable<SessionEntry> history)
        => [.. history.Where(entry => entry.Role.Equals(MessageRole.Notification))];

    /// <summary>Replay banners: system-role rows flagged as such.</summary>
    internal static IReadOnlyList<SessionEntry> BannerRows(this IEnumerable<SessionEntry> history)
        => [.. history.Where(entry => entry.Role.Equals(MessageRole.System) && entry.IsReplayBanner)];

    /// <summary>Assistant-role rows with no content: a reply that says nothing.</summary>
    internal static IReadOnlyList<SessionEntry> BlankAssistantRows(this IEnumerable<SessionEntry> history)
        => [.. history.Where(entry => entry.Role.Equals(MessageRole.Assistant) && string.IsNullOrWhiteSpace(entry.Content))];

    /// <summary>
    /// The transcript's record that the synthetic effect for <paramref name="toolCallId"/> completed: the tool
    /// row carrying the tool's own result, not its start marker or an interruption stand-in.
    /// </summary>
    internal static IReadOnlyList<SessionEntry> ToolReceipts(this IEnumerable<SessionEntry> history, string toolCallId)
        =>
        [
            .. history.Where(entry => entry.Role.Equals(MessageRole.Tool)
                && entry.ToolCallId == toolCallId
                && !entry.ToolIsError
                && !entry.ToolIsIncomplete
                && entry.Content.Mentions(SideEffectToolContributor.Receipt(toolCallId)))
        ];

    /// <summary>True when the transcript still carries a crash sentinel.</summary>
    internal static bool HasCrashSentinel(this IEnumerable<SessionEntry> history)
        => history.Any(entry => entry.IsCrashSentinel);

    /// <summary>Case-insensitive containment that tolerates missing text.</summary>
    internal static bool Mentions(this string? text, string needle)
        => text?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>One line per row, for assertion messages.</summary>
    internal static string Render(this IEnumerable<SessionEntry> history)
        => string.Join(
            Environment.NewLine,
            history.Select(entry =>
            {
                var flags = entry.IsCrashSentinel ? "|sentinel" : entry.IsReplayBanner ? "|banner" : string.Empty;
                var content = entry.Content.ReplaceLineEndings(" ");
                return $"  [{entry.Role}{flags}] {(content.Length > 140 ? content[..140] + "..." : content)}";
            }));
}
