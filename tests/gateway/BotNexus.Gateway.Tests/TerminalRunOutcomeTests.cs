using BotNexus.Agent.Core.ExtensionPoints.RunCompletion;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Isolation;
using BotNexus.Gateway.Sessions;
using BotNexus.Gateway.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using DomainAgentRunId = BotNexus.Domain.Primitives.AgentRunId;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// The terminal-outcome helper is the one place that decides what a failed run is, what it leaves in the
/// transcript and when it may be announced, so the streaming and blocking branches cannot drift apart.
/// </summary>
public sealed class TerminalRunOutcomeTests
{
    private const string MessageId = "message-terminal-outcome";
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    // Proves: only a Failed completion is a failure, whatever its casing; every other status is not.
    [Theory]
    [InlineData("Failed", true)]
    [InlineData("failed", true)]
    [InlineData("FAILED", true)]
    [InlineData("Completed", false)]
    [InlineData("Parked", false)]
    [InlineData("Cancelled", false)]
    [InlineData("IncompleteWithoutStopReason", false)]
    [InlineData("", false)]
    public void IsFailure_IsTrueOnlyForAFailedCompletion(string status, bool expected)
        => TerminalRunOutcome.IsFailure(Signal(status)).ShouldBe(expected);

    // Proves: a missing completion (a strategy that reports none) is not classified as a failure.
    [Fact]
    public void IsFailure_WithoutACompletion_IsFalse()
        => TerminalRunOutcome.IsFailure(null).ShouldBeFalse();

    // Proves: a provider error on the terminal turn is described by the blocking branch with the words the stream boundary emits.
    [Fact]
    public void ResolveDetail_TurnError_MatchesTheStreamBoundary()
    {
        var turn = new AssistantAgentMessage("partial", FinishReason: StopReason.Error, ErrorMessage: "provider rejected the request");

        AssertBothBoundariesAgree(turn, Failed("provider rejected the request"), "provider rejected the request");
    }

    // Proves: a terminal turn error with no text is still explained, with the stream boundary's own fallback.
    [Fact]
    public void ResolveDetail_TurnErrorWithoutText_MatchesTheStreamBoundary()
    {
        var turn = new AssistantAgentMessage(string.Empty, FinishReason: StopReason.Error);

        AssertBothBoundariesAgree(turn, Failed(null), "The provider ended the turn with an error but supplied no detail.");
    }

    // Proves: a run that failed without any turn error (for example required compaction) reports the completion's detail.
    [Fact]
    public void ResolveDetail_RunFailureWithoutATurnError_UsesTheCompletionDetail()
        => AssertBothBoundariesAgree(null, Failed("Required proactive compaction did not apply."), "Required proactive compaction did not apply.");

    // Proves: a failure that supplies no detail anywhere gets the same fallback sentence on both boundaries.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveDetail_NoDetailAnywhere_UsesTheSameFallback(string? detail)
        => AssertBothBoundariesAgree(null, Failed(detail), "The agent run failed but supplied no detail.");

    // Proves: a blank terminal error never hides a useful completion detail.
    [Fact]
    public void ResolveDetail_BlankTerminalError_FallsBackToTheCompletionDetail()
        => TerminalRunOutcome.ResolveDetail("  ", Signal("Failed", "the run's own detail")).ShouldBe("the run's own detail");

    // Proves: the failure row is a plain system row that carries the run id and the stream boundary's error text.
    [Fact]
    public void CreateErrorEntry_IsASystemRowWithTheRunIdAndTheStreamErrorText()
    {
        var runId = DomainAgentRunId.Create();

        var entry = TerminalRunOutcome.CreateErrorEntry(runId, "provider rejected the request");

        entry.Role.ShouldBe(MessageRole.System);
        entry.Content.ShouldBe("Agent stream error: provider rejected the request");
        entry.AgentRunId.ShouldBe(runId);
        entry.IsCrashSentinel.ShouldBeFalse();
    }

    // Proves: an event is announced with the requested type, for the right session, once a save persisted.
    [Theory]
    [InlineData(SessionLifecycleEventType.TerminalFailure)]
    [InlineData(SessionLifecycleEventType.Closed)]
    public async Task PublishAfterSaveAsync_PersistedSave_AnnouncesTheSessionOnce(SessionLifecycleEventType type)
    {
        var (events, published) = Subscribe();
        var session = NewSession();

        await TerminalRunOutcome.PublishAfterSaveAsync(events, session, type, SessionSaveOutcome.Persisted, CancellationToken.None);

        var announced = published.ShouldHaveSingleItem();
        announced.Type.ShouldBe(type);
        announced.SessionId.ShouldBe(session.SessionId.Value);
        announced.AgentId.ShouldBe(session.AgentId.Value);
        announced.Session.ShouldBeSameAs(session);
    }

    // Proves: a skipped, lost or missing save announces nothing, because the evidence a subscriber would act on is not durable.
    [Fact]
    public async Task PublishAfterSaveAsync_AnythingButAPersistedSave_AnnouncesNothing()
    {
        var (events, published) = Subscribe();
        var session = NewSession();

        foreach (var outcome in Enum.GetValues<SessionSaveOutcome>().Where(value => value != SessionSaveOutcome.Persisted))
        {
            await TerminalRunOutcome.PublishAfterSaveAsync(events, session, SessionLifecycleEventType.TerminalFailure, outcome, CancellationToken.None);
        }

        await TerminalRunOutcome.PublishAfterSaveAsync(events, session, SessionLifecycleEventType.TerminalFailure, null, CancellationToken.None);

        published.ShouldBeEmpty();
    }

    // Proves: a host built without a lifecycle channel runs the same path and announces to nobody.
    [Fact]
    public async Task PublishAfterSaveAsync_WithoutALifecycleChannel_DoesNothing()
        => await Should.NotThrowAsync(() => TerminalRunOutcome.PublishAfterSaveAsync(
            null, NewSession(), SessionLifecycleEventType.TerminalFailure, SessionSaveOutcome.Persisted, CancellationToken.None));

    // Proves: a failure is recorded on the run's own lease and on no other, so a follow-up's or a later run's lease can never be marked by mistake.
    [Fact]
    public void MarkLeaseFailed_RecordsTheFailureOnTheRunsOwnLeaseOnly()
    {
        var session = NewSession();
        var ownLease = AddLeasedSentinel(session, "first message");
        var otherLease = AddLeasedSentinel(session, "second message");
        var runId = DomainAgentRunId.Create();

        var marked = TerminalRunOutcome.MarkLeaseFailed(session, ownLease.LeaseId, "provider rejected the request", runId);

        marked.ShouldBeTrue();
        var leases = session.GetHistorySnapshot().Where(entry => entry.IsCrashSentinel)
            .Select(entry => entry.RunLease.ShouldNotBeNull()).ToList();
        leases.Count.ShouldBe(2);
        leases[0].LeaseId.ShouldBe(ownLease.LeaseId);
        leases[0].State.ShouldBe(RunLeaseState.Failed);
        leases[0].FailureDetail.ShouldBe("provider rejected the request");
        leases[0].FailedRunId.ShouldBe(runId);
        leases[0].FailedAt.ShouldNotBeNull();
        leases[1].ShouldBe(otherLease, "another run's lease must be left exactly as it was");
    }

    // Proves: a run started without a lease, or whose lease is no longer on the session, marks nothing and says so.
    [Fact]
    public void MarkLeaseFailed_WithoutAMatchingLease_ReturnsFalseAndChangesNothing()
    {
        var session = NewSession();
        var lease = AddLeasedSentinel(session, "first message");
        var before = session.GetHistorySnapshot();

        TerminalRunOutcome.MarkLeaseFailed(session, null, "boom", null).ShouldBeFalse("no lease id");
        TerminalRunOutcome.MarkLeaseFailed(session, RunLeaseId.Create(), "boom", null).ShouldBeFalse("a lease that is not on the session");

        session.GetHistorySnapshot().ShouldBe(before);
        session.GetHistorySnapshot().Single(entry => entry.IsCrashSentinel).RunLease.ShouldBe(lease);
    }

    // Proves: the failure detail a lease keeps is bounded by the lease itself, however long the run's error text was.
    [Fact]
    public void MarkLeaseFailed_BoundsALongFailureDetail()
    {
        var session = NewSession();
        var lease = AddLeasedSentinel(session, "first message");

        TerminalRunOutcome.MarkLeaseFailed(session, lease.LeaseId, new string('x', 5000), null).ShouldBeTrue();

        var detail = session.GetHistorySnapshot().Single(entry => entry.IsCrashSentinel).RunLease.ShouldNotBeNull().FailureDetail.ShouldNotBeNull();
        detail.Length.ShouldBeLessThanOrEqualTo(RunLease.MaxFailureDetailLength);
    }

    // The blocking boundary reads the settled response; the stream boundary emits an Error event first from the terminal turn, else at run end.
    private static void AssertBothBoundariesAgree(AssistantAgentMessage? terminalTurn, RunCompletionResult completion, string expected)
    {
        AgentMessage[] messages = terminalTurn is null ? [] : [terminalTurn];

        var response = InProcessAgentHandle.BuildResponse(messages, completion);
        var blocking = TerminalRunOutcome.ResolveDetail(response.TerminalError, response.Completion);

        var turnError = terminalTurn is null
            ? null
            : InProcessAgentHandle.MapTurnError(new TurnEndEvent(terminalTurn, [], Now), MessageId);
        var runError = InProcessAgentHandle.MapRunError(
            new AgentEndEvent(messages, null, Now, completion), MessageId, errorAlreadyEmitted: turnError is not null);
        var streamed = (turnError ?? runError).ShouldNotBeNull().ErrorMessage;

        blocking.ShouldBe(streamed, "the blocking branch must describe the failure with the stream boundary's words");
        blocking.ShouldBe(expected);
    }

    private static RunCompletionResult Failed(string? detail)
        => new(RunCompletionStatus.Failed, [], Detail: detail);

    private static RunCompletionSignal Signal(string status, string? detail = null)
        => new(status, [], null, detail, null, null, null, 0);

    private static GatewaySession NewSession()
        => new() { SessionId = SessionId.From("session-terminal-outcome"), AgentId = AgentId.From("agent-terminal-outcome") };

    // An inbound message and the sentinel the host would write for it, as a run starting now would leave them.
    private static RunLease AddLeasedSentinel(GatewaySession session, string content)
    {
        var input = new SessionEntry { Role = MessageRole.User, Content = content };
        session.AddEntry(input);
        var lease = RunLease.Admit(InputRef.For(input));
        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "[agent turn in progress]", IsCrashSentinel = true, RunLease = lease });
        return lease;
    }

    private static (SessionLifecycleEvents Events, List<SessionLifecycleEvent> Published) Subscribe()
    {
        var events = new SessionLifecycleEvents(NullLogger<SessionLifecycleEvents>.Instance);
        var published = new List<SessionLifecycleEvent>();
        events.SessionChanged += (lifecycleEvent, _) =>
        {
            published.Add(lifecycleEvent);
            return Task.CompletedTask;
        };
        return (events, published);
    }
}
