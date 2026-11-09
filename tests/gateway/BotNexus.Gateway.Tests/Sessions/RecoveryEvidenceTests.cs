using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Audit;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// What interrupted work is known to have done, read from the transcript: finished calls kept apart from
/// calls whose outcome is unknown, bounded, and windowed to the work since the input was first sent.
/// </summary>
public sealed class RecoveryEvidenceTests
{
    // Proves: a call with a start row and a real result is completed; one with only a start row is unknown.
    [Fact]
    public void Collect_SeparatesFinishedCallsFromCallsWithoutAResult()
    {
        var history = new[]
        {
            Start("call-done", "update_record", "{\"id\":1}"),
            Result("call-done", "update_record", "updated"),
            Start("call-open", "send_report", "{}")
        };

        var (completed, unknown) = RecoveryEvidence.Collect(history);

        completed.ShouldHaveSingleItem().ToolCallId.ShouldBe("call-done");
        completed[0].ToolName.ShouldBe("update_record");
        completed[0].Arguments.ShouldBe("{\"id\":1}");
        unknown.ShouldHaveSingleItem().ToolCallId.ShouldBe("call-open");
    }

    // Proves: a synthesized "did not complete" result, which records that nothing was observed, is unknown, not completed.
    [Fact]
    public void Collect_TreatsASynthesizedResultAsOutcomeUnknown()
    {
        var history = new[]
        {
            Start("call-cut", "update_record", "{}"),
            Result("call-cut", "update_record", "Tool 'update_record' did not complete — result synthesized for transcript consistency.", isError: true),
            Start("call-flagged", "send_report", "{}"),
            Result("call-flagged", "send_report", "interrupted", incomplete: true)
        };

        var (completed, unknown) = RecoveryEvidence.Collect(history);

        completed.ShouldBeEmpty();
        unknown.Select(call => call.ToolCallId).ShouldBe(new[] { "call-cut", "call-flagged" });
    }

    // Proves: only the work after the origin input counts, so earlier turns' tool calls are not offered as evidence for this one.
    [Fact]
    public void Collect_WithAnOrigin_ReadsOnlyTheWorkAfterIt()
    {
        var earlier = new[] { Start("call-earlier", "old_tool", "{}"), Result("call-earlier", "old_tool", "ok") };
        var origin = new SessionEntry { Role = MessageRole.Assistant, Content = "Apply the change." };
        var since = new[] { Start("call-since", "new_tool", "{}"), Result("call-since", "new_tool", "ok") };
        IReadOnlyList<SessionEntry> history = [.. earlier, origin, .. since];

        var (completed, _) = RecoveryEvidence.Collect(history, origin);

        completed.ShouldHaveSingleItem().ToolCallId.ShouldBe("call-since");
    }

    // Proves: the window covers every attempt since the origin, so a replacement hears of effects earlier replacements applied.
    [Fact]
    public void Collect_WithAnOrigin_IncludesEffectsFromEarlierReplacementAttempts()
    {
        var origin = new SessionEntry { Role = MessageRole.Assistant, Content = "Apply the change." };
        var replacementCopy = new SessionEntry { Role = MessageRole.Assistant, Content = "Apply the change." };
        IReadOnlyList<SessionEntry> history =
        [
            origin,
            Start("call-first", "update_record", "{}"), Result("call-first", "update_record", "ok"),
            replacementCopy,
            Start("call-second", "send_report", "{}"), Result("call-second", "send_report", "ok")
        ];

        var (completed, _) = RecoveryEvidence.Collect(history, origin);

        completed.Select(call => call.ToolCallId).ShouldBe(new[] { "call-first", "call-second" });
    }

    // Proves: each list is bounded; completed work keeps the newest calls, unknown calls keep the first ones.
    [Fact]
    public void Collect_BoundsEachList()
    {
        var history = new List<SessionEntry>();
        for (var index = 0; index < RecoveryEvidence.MaxCallsPerList + 5; index++)
        {
            history.Add(Start($"done-{index}", "tool", "{}"));
            history.Add(Result($"done-{index}", "tool", "ok"));
            history.Add(Start($"open-{index}", "tool", "{}"));
        }

        var (completed, unknown) = RecoveryEvidence.Collect(history);

        completed.Count.ShouldBe(RecoveryEvidence.MaxCallsPerList);
        completed[^1].ToolCallId.ShouldBe($"done-{RecoveryEvidence.MaxCallsPerList + 4}", "the newest finished calls are kept");
        unknown.Count.ShouldBe(RecoveryEvidence.MaxCallsPerList);
        unknown[0].ToolCallId.ShouldBe("open-0");
    }

    // Proves: the arguments preview is one short bounded line, so a large or multi-line argument cannot flood the prompt.
    [Fact]
    public void Collect_BoundsAndFlattensTheArgumentsPreview()
    {
        var history = new[]
        {
            Start("call-big", "tool", "{\"text\":\"line one\\nline two\",\"pad\":\"" + new string('x', 1000) + "\"}"),
            Result("call-big", "tool", "ok")
        };

        var preview = RecoveryEvidence.Collect(history).Completed.ShouldHaveSingleItem().Arguments.ShouldNotBeNull();

        preview.Length.ShouldBeLessThanOrEqualTo(160);
        preview.ShouldNotContain("\n");
        preview.ShouldEndWith("...");
    }

    // Proves: rows built by the production audit sink in every shape a transcript holds give the call's real arguments: a streamed pair, a streamed result whose own arguments are only the sink's placeholder, and a blocking run's result-only row.
    [Fact]
    public void Collect_ReadsTheRealArguments_FromRowsBuiltByTheAuditSink_InEveryShape()
    {
        var sink = DefaultToolAuditSink.Instance;
        var pairedStart = sink.ProjectStart("call-paired", "update_record", "{\"id\":1}");
        var history = new List<SessionEntry>
        {
            // Streamed and paired: the result repeats the start's arguments.
            pairedStart,
            sink.ProjectResult("call-paired", "update_record", "updated", isError: false, maxPersistedBytes: 0, pairedStart.ToolArgs),
            // Streamed but unpaired: the result row only has the sink's placeholder.
            sink.ProjectStart("call-unpaired", "update_record", "{\"id\":2}"),
            sink.ProjectResult("call-unpaired", "update_record", "updated", isError: false, maxPersistedBytes: 0)
        };
        // A blocking run records result rows only, each carrying its own arguments.
        history.AddRange(sink.ProjectBlockingRun(sink.CaptureBlockingRun(new AgentResponse
        {
            Content = "done",
            ToolCalls = [new AgentToolCallInfo("call-blocking", "update_record", IsError: false, Arguments: "{\"id\":3}", ResultContent: "updated")]
        })));

        var (completed, unknown) = RecoveryEvidence.Collect(history);

        completed.Select(call => call.ToolCallId).ShouldBe(new[] { "call-paired", "call-unpaired", "call-blocking" });
        completed.Select(call => call.Arguments).ShouldBe(new[] { "{\"id\":1}", "{\"id\":2}", "{\"id\":3}" });
        unknown.ShouldBeEmpty();
    }

    // Proves: a call that really took no arguments is reported with the empty object, not as a call whose arguments are missing.
    [Fact]
    public void Collect_ReportsACallThatTookNoArgumentsAsTheEmptyObject()
    {
        var sink = DefaultToolAuditSink.Instance;
        var history = new[]
        {
            sink.ProjectStart("call-bare", "send_report", null),
            sink.ProjectResult("call-bare", "send_report", "sent", isError: false, maxPersistedBytes: 0)
        };

        RecoveryEvidence.Collect(history).Completed.ShouldHaveSingleItem().Arguments.ShouldBe(DefaultToolAuditSink.NoArguments);
    }

    // Proves: when only the result row has real arguments they are used, so a placeholder on the start row does not hide them either.
    [Fact]
    public void Collect_UsesTheResultArguments_WhenTheStartRowHasOnlyThePlaceholder()
    {
        var sink = DefaultToolAuditSink.Instance;
        var history = new[]
        {
            sink.ProjectStart("call-late", "update_record", null),
            sink.ProjectResult("call-late", "update_record", "updated", isError: false, maxPersistedBytes: 0, "{\"id\":4}")
        };

        RecoveryEvidence.Collect(history).Completed.ShouldHaveSingleItem().Arguments.ShouldBe("{\"id\":4}");
    }

    // Proves: only a successful receipt counts as completed. An errored result, an incomplete one and one the gateway synthesized are all outcome-unknown, because none of them shows the call had no effect.
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void Collect_CountsOnlyASuccessfulReceiptAsCompleted(bool isError, bool incomplete, bool expectCompleted)
    {
        var history = new[]
        {
            Start("call-1", "update_record", "{\"id\":1}"),
            Result(
                "call-1",
                "update_record",
                isError ? "Tool 'update_record' failed: the connection was reset after the request was sent." : "updated",
                isError,
                incomplete)
        };

        var (completed, unknown) = RecoveryEvidence.Collect(history);

        (expectCompleted ? completed : unknown).ShouldHaveSingleItem().ToolCallId.ShouldBe("call-1");
        (expectCompleted ? unknown : completed).ShouldBeEmpty();
    }

    // Proves: an errored call is kept apart from a successful one beside it, and a result-only row (a blocking run records no start row) is judged the same way.
    [Fact]
    public void Collect_KeepsAnErroredCallApartFromASuccessfulOne_WhetherOrNotAStartRowExists()
    {
        var history = new[]
        {
            Start("call-done", "update_record", "{\"id\":1}"),
            Result("call-done", "update_record", "updated"),
            Result("call-errored", "send_report", "Tool 'send_report' failed: timed out after the request was sent.", isError: true)
        };

        var (completed, unknown) = RecoveryEvidence.Collect(history);

        completed.ShouldHaveSingleItem().ToolCallId.ShouldBe("call-done");
        unknown.ShouldHaveSingleItem().ToolCallId.ShouldBe("call-errored");
    }

    // Proves: a tool row without a call id, and non-tool rows, contribute nothing.
    [Fact]
    public void Collect_IgnoresRowsThatAreNotIdentifiableToolCalls()
    {
        var history = new[]
        {
            new SessionEntry { Role = MessageRole.Tool, Content = "orphan", ToolName = "tool" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "Hello." },
            new SessionEntry { Role = MessageRole.User, Content = "Hi." }
        };

        var (completed, unknown) = RecoveryEvidence.Collect(history);

        completed.ShouldBeEmpty();
        unknown.ShouldBeEmpty();
    }

    // Proves: when a later run reuses an earlier run's call id, the earlier run's call stays outcome-unknown with its own arguments and the later run's is completed with its own, instead of the later success hiding the unresolved call and the two being merged.
    [Fact]
    public void Collect_KeepsEachRunsCallApart_WhenALaterRunReusesTheCallId()
    {
        var history = new[]
        {
            // Run A issued call-1 and never recorded a result for it.
            InRun(Start("call-1", "update_record", "{\"id\":1}"), RunA),
            // Run B, a replacement, used the same id for a different call that finished.
            InRun(Start("call-1", "update_record", "{\"id\":2}"), RunB),
            InRun(Result("call-1", "update_record", "updated"), RunB)
        };

        var (completed, unknown) = RecoveryEvidence.Collect(history);

        var finished = completed.ShouldHaveSingleItem();
        finished.ToolCallId.ShouldBe("call-1");
        finished.Arguments.ShouldBe("{\"id\":2}", "the finished call is the later run's, with its own arguments");
        var unresolved = unknown.ShouldHaveSingleItem();
        unresolved.ToolCallId.ShouldBe("call-1");
        unresolved.Arguments.ShouldBe("{\"id\":1}", "the earlier run's call keeps its own arguments and stays to be verified");
    }

    // Proves: the same holds when the later run's record is a result-only row, which is how a blocking run records its calls.
    [Fact]
    public void Collect_KeepsEachRunsCallApart_WhenTheLaterRunLeftOnlyAResultRow()
    {
        var history = new[]
        {
            InRun(Start("call-1", "update_record", "{\"id\":1}"), RunA),
            InRun(Result("call-1", "update_record", "updated") with { ToolArgs = "{\"id\":2}" }, RunB)
        };

        var (completed, unknown) = RecoveryEvidence.Collect(history);

        completed.ShouldHaveSingleItem().Arguments.ShouldBe("{\"id\":2}");
        unknown.ShouldHaveSingleItem().Arguments.ShouldBe("{\"id\":1}");
    }

    // Proves: a later run's unresolved call is not hidden by an earlier run's finished call that has the same id.
    [Fact]
    public void Collect_KeepsALaterRunsUnresolvedCall_WhenAnEarlierRunFinishedOneWithTheSameId()
    {
        var history = new[]
        {
            InRun(Start("call-1", "update_record", "{\"id\":1}"), RunA),
            InRun(Result("call-1", "update_record", "updated"), RunA),
            InRun(Start("call-1", "update_record", "{\"id\":2}"), RunB)
        };

        var (completed, unknown) = RecoveryEvidence.Collect(history);

        completed.ShouldHaveSingleItem().Arguments.ShouldBe("{\"id\":1}");
        unknown.ShouldHaveSingleItem().Arguments.ShouldBe("{\"id\":2}");
    }

    // Proves: a row that names no run is never paired with a row of a run: a run's start whose only result is unattributed stays outcome-unknown, and the unattributed result is judged on its own.
    [Fact]
    public void Collect_NeverPairsARowWithoutARun_WithARowOfARun()
    {
        var history = new[]
        {
            InRun(Start("call-1", "update_record", "{\"id\":1}"), RunA),
            Result("call-1", "update_record", "updated")
        };

        var (completed, unknown) = RecoveryEvidence.Collect(history);

        unknown.ShouldHaveSingleItem().Arguments.ShouldBe("{\"id\":1}");
        completed.ShouldHaveSingleItem().ToolCallId.ShouldBe("call-1");
    }

    // Proves: rows that name no run are paired only within one uninterrupted stretch of tool rows, so a start before another run's inbound row and a result after it are not taken for one call.
    [Fact]
    public void Collect_PairsRowsWithoutARunOnlyWithinOneStretchOfToolRows()
    {
        var history = new[]
        {
            Start("call-1", "update_record", "{\"id\":1}"),
            new SessionEntry { Role = MessageRole.Assistant, Content = "Apply the change." },
            Start("call-1", "update_record", "{\"id\":2}"),
            Result("call-1", "update_record", "updated")
        };

        var (completed, unknown) = RecoveryEvidence.Collect(history);

        unknown.ShouldHaveSingleItem().Arguments.ShouldBe("{\"id\":1}");
        completed.ShouldHaveSingleItem().Arguments.ShouldBe("{\"id\":2}");
    }

    // Proves: an unpaired start and its own synthesized result, separated by a boundary row, are told once as one unresolved call, not twice.
    [Fact]
    public void Collect_TellsAnUnresolvedCallOnce_WhenItsStartAndItsSynthesizedResultAreSeparated()
    {
        var history = new[]
        {
            Start("call-cut", "update_record", "{}"),
            new SessionEntry { Role = MessageRole.System, Content = "[agent turn in progress]", IsCrashSentinel = true },
            Result("call-cut", "update_record", "Tool 'update_record' did not complete — result synthesized for transcript consistency.", isError: true)
        };

        var (completed, unknown) = RecoveryEvidence.Collect(history);

        completed.ShouldBeEmpty();
        unknown.ShouldHaveSingleItem().ToolCallId.ShouldBe("call-cut");
    }

    private static readonly AgentRunId RunA = AgentRunId.From("run-a");
    private static readonly AgentRunId RunB = AgentRunId.From("run-b");

    // The row as the run that made it recorded it: production stamps every tool row with its run.
    private static SessionEntry InRun(SessionEntry row, AgentRunId run) => row with { AgentRunId = run };

    private static SessionEntry Start(string id, string tool, string args) => new()
    {
        Role = MessageRole.Tool,
        Content = string.Empty,
        ToolName = tool,
        ToolCallId = id,
        ToolArgs = args,
        Kind = MessageKind.ToolStart
    };

    // A result row built without the call's arguments, the way the audit sink builds one it was not given
    // them for: it carries the sink's "{}" placeholder, which must never hide the start row's arguments.
    private static SessionEntry Result(string id, string tool, string content, bool isError = false, bool incomplete = false) => new()
    {
        Role = MessageRole.Tool,
        Content = content,
        ToolName = tool,
        ToolCallId = id,
        ToolArgs = "{}",
        ToolIsError = isError,
        ToolIsIncomplete = incomplete,
        Kind = MessageKind.ToolResult
    };
}
