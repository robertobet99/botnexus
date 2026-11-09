using System.Text;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Renders what recovery knows about interrupted work as a block for the model, placed ahead of the
/// original input in the provider-bound message only. The transcript keeps exactly the words that were
/// sent: this text is never stored, so it cannot be mistaken for something the user or caller wrote.
/// </summary>
/// <remarks>
/// A warm agent still holds the failed turn in memory but has no reason to treat it as unfinished; a cold
/// one has only user and assistant text and no record of any tool call. In both cases the replacement
/// would otherwise look like a fresh request, and a fresh request for a mutation is a second mutation.
/// The block states what finished, what is unknown, and asks for verification before any repetition. It
/// does not repeat the original input, which follows it verbatim.
/// </remarks>
internal static class RecoveryFraming
{
    /// <summary>
    /// Returns <paramref name="originalContent"/> preceded by the recovery block for
    /// <paramref name="recovery"/>.
    /// </summary>
    /// <param name="recovery">What recovery knows about the interrupted work.</param>
    /// <param name="originalContent">The original input, exactly as it was stored.</param>
    /// <returns>The provider-bound text: the block first, then the original input unchanged.</returns>
    internal static string Prepend(RecoveryContext recovery, string originalContent)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        return Render(recovery) + "\n\n" + originalContent;
    }

    private static string Render(RecoveryContext recovery)
    {
        var block = new StringBuilder();
        block.Append("[AUTOMATIC RECOVERY ")
            .Append(recovery.Attempt)
            .Append(" of ")
            .Append(recovery.MaxAttempts)
            .Append("]\n")
            .Append("Your previous attempt at the request below was cut off before it finished, at a point the ")
            .Append("gateway cannot see. Some of its work has already happened. Do not assume the request still ")
            .Append("has to be done from the start, and do not repeat work that is already done.");

        if (recovery.CompletedCalls.Count > 0)
        {
            block.Append("\n\nAlready completed. Do not repeat these:");
            foreach (var call in recovery.CompletedCalls)
                AppendCall(block, call);
        }

        if (recovery.OutcomeUnknownCalls.Count > 0)
        {
            block.Append("\n\nStarted, but they did not finish cleanly: no result was recorded, or the result was an error ")
                .Append("or incomplete. Whether each one took effect is unknown, so inspect the current state to ")
                .Append("confirm before you consider doing it again:");
            foreach (var call in recovery.OutcomeUnknownCalls)
                AppendCall(block, call);
        }

        block.Append("\n\nBefore you take any action that writes, changes state or is otherwise not safely repeatable, ")
            .Append("verify the current state directly with your tools, then continue from where you actually are. ")
            .Append("The original request follows, unchanged.");
        return block.ToString();
    }

    private static void AppendCall(StringBuilder block, RecoveredToolCall call)
    {
        block.Append("\n- ").Append(call.ToolName).Append(" (call ").Append(call.ToolCallId).Append(')');
        if (!string.IsNullOrEmpty(call.Arguments))
            block.Append(" with arguments ").Append(call.Arguments);
    }
}
