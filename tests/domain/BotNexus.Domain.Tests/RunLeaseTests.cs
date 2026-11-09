using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Domain.Tests;

public sealed class RunLeaseTests
{
    private static readonly DateTimeOffset Recorded = new(2026, 11, 8, 23, 1, 31, TimeSpan.Zero);

    [Fact]
    public void Admit_CreatesARunningAttemptZeroLeaseWhoseOriginIsItsInput()
    {
        var input = Input("Apply the scheduled change.");

        var lease = RunLease.Admit(input);

        lease.State.ShouldBe(RunLeaseState.Running);
        lease.Attempt.ShouldBe(0);
        lease.Input.ShouldBe(input);
        lease.Origin.ShouldBe(input);
        lease.InterruptedLeaseId.ShouldBeNull();
        lease.FailedAt.ShouldBeNull();
        lease.FailureDetail.ShouldBeNull();
        lease.FailedRunId.ShouldBeNull();
    }

    [Fact]
    public void Admit_MintsADistinctLeaseEachTime()
    {
        var input = Input("Apply the scheduled change.");

        RunLease.Admit(input).LeaseId.ShouldNotBe(RunLease.Admit(input).LeaseId);
    }

    [Fact]
    public void MarkFailed_RecordsTheFailureAndKeepsTheLeasesIdentity()
    {
        var lease = RunLease.Admit(Input("Apply the scheduled change."));
        var runId = AgentRunId.From("run-1");

        var failed = lease.MarkFailed(Recorded, "The backend rejected the request.", runId);

        failed.State.ShouldBe(RunLeaseState.Failed);
        failed.FailedAt.ShouldBe(Recorded);
        failed.FailureDetail.ShouldBe("The backend rejected the request.");
        failed.FailedRunId.ShouldBe(runId);
        failed.LeaseId.ShouldBe(lease.LeaseId);
        failed.Attempt.ShouldBe(lease.Attempt);
        failed.Input.ShouldBe(lease.Input);
        failed.Origin.ShouldBe(lease.Origin);
        lease.State.ShouldBe(RunLeaseState.Running, "the original lease value is immutable");
    }

    [Theory]
    [InlineData(RunLeaseState.Failed)]
    [InlineData(RunLeaseState.Pending)]
    public void MarkFailed_OnALeaseThatIsNotRunning_ReturnsItUnchanged(RunLeaseState state)
    {
        var lease = RunLease.Admit(Input("Apply the scheduled change.")) with { State = state };

        var result = lease.MarkFailed(Recorded, "A later failure.", AgentRunId.From("run-2"));

        result.ShouldBeSameAs(lease);
    }

    [Theory]
    [InlineData(511, 511)]
    [InlineData(512, 512)]
    [InlineData(513, 512)]
    [InlineData(5000, 512)]
    public void MarkFailed_BoundsTheDetailToTheLeasesLimit(int suppliedLength, int expectedLength)
    {
        var lease = RunLease.Admit(Input("Apply the scheduled change.")).MarkFailed(Recorded, new string('x', suppliedLength), null);

        var detail = lease.FailureDetail.ShouldNotBeNull();
        detail.Length.ShouldBe(expectedLength);
        detail.EndsWith("...", StringComparison.Ordinal).ShouldBe(suppliedLength > RunLease.MaxFailureDetailLength);
    }

    [Fact]
    public void MarkFailed_KeepsTheDetailOnOneControlCharacterFreeLine()
    {
        var lease = RunLease.Admit(Input("Apply the scheduled change.")).MarkFailed(Recorded, "first line\r\nsecond\tline\u0007 third", null);

        lease.FailureDetail.ShouldBe("first line second line third");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n\t ")]
    public void MarkFailed_StoresNoDetailWhenNoneWasSupplied(string? detail)
    {
        var lease = RunLease.Admit(Input("Apply the scheduled change.")).MarkFailed(Recorded, detail, null);

        lease.State.ShouldBe(RunLeaseState.Failed);
        lease.FailureDetail.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Json_RoundTripsEveryFieldToAnEqualLease(bool camelCase)
    {
        var options = camelCase ? new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase } : new JsonSerializerOptions();
        var original = RunLease.Admit(Input("Apply the scheduled change."))
            .MarkFailed(Recorded, "The backend rejected the request.", AgentRunId.From("run-1"))
            with
        {
            Attempt = 2,
            Origin = Input("The first message."),
            InterruptedLeaseId = RunLeaseId.Create()
        };

        var json = JsonSerializer.Serialize(original, options);
        var readBack = JsonSerializer.Deserialize<RunLease>(json, options).ShouldNotBeNull();

        readBack.ShouldBe(original);
    }

    [Fact]
    public void Json_WritesTheStateAsAReadableString()
    {
        var json = JsonSerializer.Serialize(RunLease.Admit(Input("Apply the scheduled change.")));

        json.ShouldContain("\"State\":\"Running\"");
    }

    [Fact]
    public void Json_RejectsADocumentMissingARequiredMember()
    {
        Action act = () => JsonSerializer.Deserialize<RunLease>("{\"State\":\"Running\"}");

        act.ShouldThrow<JsonException>();
    }

    [Fact]
    public void QueueSuccessor_ReservesTheNextAttemptWithAFreshIdentityAndTheSameOrigin()
    {
        var origin = Input("The first message.");
        var failed = RunLease.Admit(origin).MarkFailed(Recorded, "The backend rejected the request.", AgentRunId.From("run-1"));

        var successor = failed.QueueSuccessor("instance-a");

        successor.LeaseId.ShouldNotBe(failed.LeaseId);
        successor.State.ShouldBe(RunLeaseState.Pending);
        successor.Attempt.ShouldBe(1);
        successor.Origin.ShouldBe(origin, "the origin is carried over unchanged");
        successor.Input.ShouldBe(origin, "until the replacement runs, the input is what it will re-send");
        successor.InterruptedLeaseId.ShouldBe(failed.LeaseId);
        successor.QueuedBy.ShouldBe("instance-a");
        successor.FailedAt.ShouldBeNull();
        successor.FailureDetail.ShouldBeNull();
        successor.FailedRunId.ShouldBeNull();
        failed.State.ShouldBe(RunLeaseState.Failed, "the superseded lease value is immutable");
    }

    [Fact]
    public void QueueSuccessor_OfAReplacement_StillResolvesToTheOriginalInputAndCountsOneMore()
    {
        var origin = Input("The first message.");
        var second = RunLease.Admit(origin).QueueSuccessor("instance-a").Adopt(Input("The first message, again."));

        var third = second.MarkFailed(Recorded, "Failed again.", null).QueueSuccessor("instance-a");

        third.Attempt.ShouldBe(2);
        third.Origin.ShouldBe(origin);
        third.Input.ShouldBe(origin);
    }

    [Fact]
    public void QueueSuccessor_RequiresAnInstanceToken()
    {
        Action act = () => RunLease.Admit(Input("The first message.")).QueueSuccessor(" ");

        act.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void Adopt_TurnsAPendingLeaseIntoARunningOneForTheReplacementsOwnInput()
    {
        var origin = Input("The first message.");
        var pending = RunLease.Admit(origin).QueueSuccessor("instance-a");
        var replacementInput = Input("The first message, again.");

        var running = pending.Adopt(replacementInput);

        running.LeaseId.ShouldBe(pending.LeaseId, "the reservation keeps its identity");
        running.State.ShouldBe(RunLeaseState.Running);
        running.Input.ShouldBe(replacementInput);
        running.Origin.ShouldBe(origin);
        running.Attempt.ShouldBe(1);
        running.InterruptedLeaseId.ShouldBe(pending.InterruptedLeaseId);
        running.QueuedBy.ShouldBeNull("a running lease is not a reservation");
    }

    [Theory]
    [InlineData(RunLeaseState.Running)]
    [InlineData(RunLeaseState.Failed)]
    public void Adopt_OnALeaseThatIsNotPending_ReturnsItUnchanged(RunLeaseState state)
    {
        var lease = RunLease.Admit(Input("The first message.")) with { State = state };

        lease.Adopt(Input("A second replacement.")).ShouldBeSameAs(lease);
    }

    [Fact]
    public void Requeue_HandsAPendingReservationToAnotherInstanceWithoutSpendingAttempts()
    {
        var pending = RunLease.Admit(Input("The first message.")).QueueSuccessor("dead-instance");

        var requeued = pending.Requeue("live-instance");

        requeued.LeaseId.ShouldBe(pending.LeaseId);
        requeued.Attempt.ShouldBe(pending.Attempt);
        requeued.State.ShouldBe(RunLeaseState.Pending);
        requeued.QueuedBy.ShouldBe("live-instance");
    }

    [Fact]
    public void Requeue_OnALeaseThatIsNotPending_ReturnsItUnchanged()
    {
        var running = RunLease.Admit(Input("The first message."));

        running.Requeue("live-instance").ShouldBeSameAs(running);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Json_RoundTripsAPendingReservation(bool camelCase)
    {
        var options = camelCase ? new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase } : new JsonSerializerOptions();
        var original = RunLease.Admit(Input("The first message.")).QueueSuccessor("instance-a");

        var readBack = JsonSerializer.Deserialize<RunLease>(JsonSerializer.Serialize(original, options), options).ShouldNotBeNull();

        readBack.ShouldBe(original);
        readBack.QueuedBy.ShouldBe("instance-a");
    }

    private static InputRef Input(string content)
        => InputRef.For(new SessionEntry { Role = MessageRole.User, Content = content, Timestamp = Recorded });
}
