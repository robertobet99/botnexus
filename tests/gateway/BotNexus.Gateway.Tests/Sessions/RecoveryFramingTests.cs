using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// The block recovery puts ahead of a replacement's original input for the model alone: what finished,
/// what is unknown, and a request to verify before repeating anything.
/// </summary>
public sealed class RecoveryFramingTests
{
    private const string Original = "Apply the scheduled change exactly once, then report the outcome.";

    // Proves: the block comes first and the original input follows it exactly as it was stored, once.
    [Fact]
    public void Prepend_PutsTheBlockFirstAndTheOriginalInputVerbatimAfterIt()
    {
        var text = RecoveryFraming.Prepend(Context(), Original);

        text.ShouldEndWith("\n\n" + Original);
        text.IndexOf(Original, StringComparison.Ordinal).ShouldBe(text.LastIndexOf(Original, StringComparison.Ordinal), "the block must not quote the input");
        text.ShouldStartWith("[AUTOMATIC RECOVERY 1 of 2]");
    }

    // Proves: completed calls are named as do-not-repeat, with their tool name, call id and arguments preview.
    [Fact]
    public void Prepend_NamesCompletedCallsAsWorkNotToRepeat()
    {
        var text = RecoveryFraming.Prepend(
            Context(completed: [new RecoveredToolCall("call-1", "update_record", "{\"id\":1}")]), Original);

        text.ShouldContain("Do not repeat");
        text.ShouldContain("update_record (call call-1) with arguments {\"id\":1}");
    }

    // Proves: calls whose outcome is unknown are named as inspect-first, apart from completed ones.
    [Fact]
    public void Prepend_NamesOutcomeUnknownCallsAsVerifyFirst()
    {
        var text = RecoveryFraming.Prepend(
            Context(unknown: [new RecoveredToolCall("call-2", "send_report", null)]), Original);

        text.ShouldContain("no result was recorded");
        text.ShouldContain("inspect the current state");
        text.ShouldContain("send_report (call call-2)");
        text.ShouldNotContain("Do not repeat these", Case.Sensitive, "nothing completed, so nothing is listed as done");
    }

    // Proves: the verify-first section says it also covers calls that came back as an error or incomplete, so the model does not read an errored call as one that certainly changed nothing.
    [Fact]
    public void Prepend_ExplainsThatAnErroredOrIncompleteResultAlsoLeavesTheOutcomeUnknown()
    {
        var text = RecoveryFraming.Prepend(
            Context(unknown: [new RecoveredToolCall("call-3", "update_record", "{\"id\":3}")]), Original);

        text.ShouldContain("an error or incomplete");
        text.ShouldContain("is unknown");
        text.ShouldContain("update_record (call call-3) with arguments {\"id\":3}");
    }

    // Proves: a call id that two runs reused is named under each heading with its own arguments, so the model can tell the call that finished from the one still to be verified.
    [Fact]
    public void Prepend_NamesACallIdTwoRunsReused_UnderEachHeadingWithItsOwnArguments()
    {
        var text = RecoveryFraming.Prepend(
            Context(
                completed: [new RecoveredToolCall("call-1", "update_record", "{\"id\":2}")],
                unknown: [new RecoveredToolCall("call-1", "update_record", "{\"id\":1}")]),
            Original);

        text.ShouldContain("update_record (call call-1) with arguments {\"id\":2}");
        text.ShouldContain("update_record (call call-1) with arguments {\"id\":1}");
        text.IndexOf("{\"id\":2}", StringComparison.Ordinal)
            .ShouldBeLessThan(text.IndexOf("{\"id\":1}", StringComparison.Ordinal), "the finished call is listed first, under its own heading");
    }

    // Proves: with no tool calls at all the block still asks for verification, and lists no sections.
    [Fact]
    public void Prepend_WithNoCalls_StillAsksForVerificationAndListsNothing()
    {
        var text = RecoveryFraming.Prepend(Context(), Original);

        text.ShouldContain("verify the current state directly with your tools");
        text.ShouldNotContain("Already completed");
        text.ShouldNotContain("outcome is unknown");
    }

    // Proves: the block claims no cause, so it is true for a failed run and for a restart alike.
    [Fact]
    public void Prepend_ClaimsNeitherARestartNorAProviderFailure()
    {
        var text = RecoveryFraming.Prepend(Context(), Original);

        text.ShouldNotContain("restart", Case.Insensitive);
        text.ShouldNotContain("provider", Case.Insensitive);
    }

    private static RecoveryContext Context(
        IReadOnlyList<RecoveredToolCall>? completed = null,
        IReadOnlyList<RecoveredToolCall>? unknown = null)
        => new()
        {
            SuccessorLeaseId = RunLeaseId.Create(),
            Attempt = 1,
            MaxAttempts = 2,
            CompletedCalls = completed ?? [],
            OutcomeUnknownCalls = unknown ?? []
        };
}
