using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Audit;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// A run lease is only useful to recovery if it comes back from storage exactly as it was written and still
/// names the same stored input, in every store. These tests save a session and read it back through a store
/// instance that shares nothing with the writer except what was persisted (the in-memory store has no
/// persisted state, so it can only prove the live object). The same holds for the tool rows that recovery
/// reads its evidence from.
/// </summary>
public sealed class RunLeaseStoreRoundTripTests
{
    public static IEnumerable<object[]> Stores()
    {
        yield return ["in-memory"];
        yield return ["file"];
        yield return ["sqlite"];
    }

    // Proves: a Running lease reads back equal, and its input reference still resolves to the one stored inbound entry, in every store.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Lease_SurvivesSaveAndColdReload_AndStillNamesTheStoredInput(string kind)
    {
        using var harness = StoreHarness.Create(kind);
        var (session, lease) = NewLeasedSession("s-roundtrip", "Apply the scheduled change.");
        await harness.Open().SaveAsync(session);

        var reloaded = (await harness.Open().GetAsync(session.SessionId)).ShouldNotBeNull();

        var sentinel = reloaded.History.Single(entry => entry.IsCrashSentinel);
        sentinel.RunLease.ShouldBe(lease);
        var resolution = lease.Input.Resolve(reloaded.History);
        resolution.Match.ShouldBe(InputRefMatch.Single);
        resolution.Entry.ShouldNotBeNull().Content.ShouldBe("Apply the scheduled change.");
    }

    // Proves: an input recorded with a non-UTC offset still resolves after a reload, because identity compares the instant, not its text form.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Lease_ForAnInputRecordedWithAnOffset_StillResolvesAfterReload(string kind)
    {
        using var harness = StoreHarness.Create(kind);
        var session = NewSession("s-offset");
        var input = new SessionEntry
        {
            Role = MessageRole.User,
            Content = "Apply the scheduled change.",
            Timestamp = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-8))
        };
        session.AddEntry(input);
        var lease = RunLease.Admit(InputRef.For(input));
        session.AddEntry(Sentinel(lease));
        await harness.Open().SaveAsync(session);

        var reloaded = (await harness.Open().GetAsync(session.SessionId)).ShouldNotBeNull();

        lease.Input.Resolve(reloaded.History).Match.ShouldBe(InputRefMatch.Single);
    }

    // Proves: all three lease states and every field, including a chain (attempt, a different origin, the interrupted lease), round-trip.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task EveryLeaseStateAndField_RoundTrips(string kind)
    {
        using var harness = StoreHarness.Create(kind);
        var session = NewSession("s-states");
        var origin = new SessionEntry { Role = MessageRole.User, Content = "The first message.", Timestamp = DateTimeOffset.UtcNow.AddMinutes(-2) };
        var replay = new SessionEntry { Role = MessageRole.User, Content = "The first message.", Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1) };
        session.AddEntries([origin, replay]);
        var running = RunLease.Admit(InputRef.For(replay)) with { Attempt = 1, Origin = InputRef.For(origin), InterruptedLeaseId = RunLeaseId.Create() };
        var failed = running.MarkFailed(DateTimeOffset.UtcNow, "the backend rejected the request", AgentRunId.From("run-failed")) with { LeaseId = RunLeaseId.Create() };
        var pending = running with { LeaseId = RunLeaseId.Create(), State = RunLeaseState.Pending, Input = InputRef.For(origin), QueuedBy = "instance-a" };
        session.AddEntries([Sentinel(running), Sentinel(failed), Sentinel(pending)]);
        await harness.Open().SaveAsync(session);

        var reloaded = (await harness.Open().GetAsync(session.SessionId)).ShouldNotBeNull();

        var leases = reloaded.History.Where(entry => entry.IsCrashSentinel).Select(entry => entry.RunLease).ToList();
        leases.Count.ShouldBe(3);
        leases[0].ShouldBe(running);
        leases[1].ShouldBe(failed);
        leases[2].ShouldBe(pending);
    }

    // Proves: recording a failure on the lease and saving updates the one stored sentinel in place (no second row, same lease id), and the update survives a cold reload.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task LeaseRecordedFailed_IsUpdatedInPlace_AndSurvivesReload(string kind)
    {
        using var harness = StoreHarness.Create(kind);
        var (session, lease) = NewLeasedSession("s-failed", "Apply the scheduled change.");
        var writer = harness.Open();
        await writer.SaveAsync(session);
        var failedAt = DateTimeOffset.UtcNow;

        var marked = session.TryUpdateRunLease(lease.LeaseId, current => current.MarkFailed(failedAt, "the backend rejected the request", AgentRunId.From("run-1")));
        await writer.SaveAsync(session);

        marked.ShouldBeTrue();
        var reloaded = (await harness.Open().GetAsync(session.SessionId)).ShouldNotBeNull();
        reloaded.History.Count.ShouldBe(2, "recording the outcome must not add or drop a row");
        var failed = reloaded.History.Single(entry => entry.IsCrashSentinel).RunLease.ShouldNotBeNull();
        failed.LeaseId.ShouldBe(lease.LeaseId);
        failed.State.ShouldBe(RunLeaseState.Failed);
        failed.FailedAt.ShouldBe(failedAt);
        failed.FailureDetail.ShouldBe("the backend rejected the request");
        failed.FailedRunId.ShouldBe(AgentRunId.From("run-1"));
    }

    // Proves: handing a failed lease to a pending reservation, then to the replacement that adopts it, updates the one stored sentinel in place each time, and every step survives a cold reload.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task LeaseHandedToAReservationThenAdopted_IsUpdatedInPlaceEachTime_AndSurvivesReload(string kind)
    {
        using var harness = StoreHarness.Create(kind);
        var (session, lease) = NewLeasedSession("s-handoff", "Apply the scheduled change.");
        var writer = harness.Open();
        await writer.SaveAsync(session);
        session.TryUpdateRunLease(lease.LeaseId, current => current.MarkFailed(DateTimeOffset.UtcNow, "boom", AgentRunId.From("run-1"))).ShouldBeTrue();
        await writer.SaveAsync(session);
        RunLease? reservation = null;

        session.TryUpdateRunLease(lease.LeaseId, current => reservation = current.QueueSuccessor("instance-a")).ShouldBeTrue();
        await writer.SaveAsync(session);

        var reserved = (await harness.Open().GetAsync(session.SessionId)).ShouldNotBeNull();
        reserved.History.Count.ShouldBe(2, "the handoff must not add or drop a row");
        reserved.History.Single(entry => entry.IsCrashSentinel).RunLease.ShouldBe(reservation);
        var replacementInput = new SessionEntry { Role = MessageRole.User, Content = "Apply the scheduled change.", Timestamp = DateTimeOffset.UtcNow };
        session.AddEntry(replacementInput);
        var reservationId = reservation.ShouldNotBeNull().LeaseId;
        RunLease? running = null;

        session.TryUpdateRunLease(reservationId, current => running = current.Adopt(InputRef.For(replacementInput))).ShouldBeTrue();
        await writer.SaveAsync(session);

        var adopted = (await harness.Open().GetAsync(session.SessionId)).ShouldNotBeNull();
        var stored = adopted.History.Single(entry => entry.IsCrashSentinel).RunLease.ShouldNotBeNull();
        stored.ShouldBe(running);
        stored.State.ShouldBe(RunLeaseState.Running);
        stored.QueuedBy.ShouldBeNull();
        stored.Origin.ShouldBe(lease.Origin);
        stored.Input.Resolve(adopted.History).Entry.ShouldNotBeNull().Content.ShouldBe("Apply the scheduled change.");
    }

    // Proves: consuming one run's sentinel by lease id deletes that stored row and only that row, and the deletion survives a cold reload.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ConsumingOneLeasesSentinel_DeletesOnlyThatRow_AndSurvivesReload(string kind)
    {
        using var harness = StoreHarness.Create(kind);
        var (session, lease) = NewLeasedSession("s-consume", "Apply the scheduled change.");
        var later = RunLease.Admit(InputRef.For(new SessionEntry { Role = MessageRole.User, Content = "A later instruction." }));
        session.AddEntry(Sentinel(later));
        var writer = harness.Open();
        await writer.SaveAsync(session);

        session.TryRemoveCrashSentinel(lease.LeaseId, out _).ShouldBeTrue();
        await writer.SaveAsync(session);

        var reloaded = (await harness.Open().GetAsync(session.SessionId)).ShouldNotBeNull();
        reloaded.History.Where(entry => entry.IsCrashSentinel).ShouldHaveSingleItem().RunLease.ShouldBe(later);
    }

    // Proves: a sentinel written without a lease, and every ordinary row, read back lease-less (the shape of every row stored before leases existed).
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task LeaselessSentinelAndOrdinaryRows_ReadBackWithoutALease(string kind)
    {
        using var harness = StoreHarness.Create(kind);
        var session = NewSession("s-leaseless");
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "Hello." });
        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "[agent turn in progress]", IsCrashSentinel = true });
        await harness.Open().SaveAsync(session);

        var reloaded = (await harness.Open().GetAsync(session.SessionId)).ShouldNotBeNull();

        reloaded.History.Count.ShouldBe(2);
        reloaded.History.ShouldAllBe(entry => entry.RunLease == null);
    }

    // Proves: removing the sentinel removes its lease with it, in every store, so a clean run leaves nothing behind.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task RemovingTheSentinel_RemovesItsLease(string kind)
    {
        using var harness = StoreHarness.Create(kind);
        var (session, _) = NewLeasedSession("s-removed", "Apply the scheduled change.");
        var writer = harness.Open();
        await writer.SaveAsync(session);

        session.RemoveCrashSentinels();
        await writer.SaveAsync(session);

        var reloaded = (await harness.Open().GetAsync(session.SessionId)).ShouldNotBeNull();
        reloaded.History.ShouldNotContain(entry => entry.IsCrashSentinel);
        reloaded.History.ShouldAllBe(entry => entry.RunLease == null);
    }

    // Proves: tool rows built by the audit sink come back from every store with the call's real arguments on the rows recovery reads, so the verify-first evidence names what each completed call was given however the transcript was stored.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task ToolRowsBuiltByTheAuditSink_ReloadWithTheirArguments_SoRecoveryEvidenceNamesThem(string kind)
    {
        using var harness = StoreHarness.Create(kind);
        var session = NewSession("s-tools");
        var sink = DefaultToolAuditSink.Instance;
        var pairedStart = sink.ProjectStart("call-paired", "update_record", "{\"id\":1}");
        session.AddEntries(
        [
            pairedStart,
            sink.ProjectResult("call-paired", "update_record", "updated", isError: false, maxPersistedBytes: 0, pairedStart.ToolArgs),
            sink.ProjectStart("call-unpaired", "update_record", "{\"id\":2}"),
            sink.ProjectResult("call-unpaired", "update_record", "updated", isError: false, maxPersistedBytes: 0),
            .. sink.ProjectBlockingRun(sink.CaptureBlockingRun(new AgentResponse
            {
                Content = "done",
                ToolCalls = [new AgentToolCallInfo("call-blocking", "update_record", IsError: false, Arguments: "{\"id\":3}", ResultContent: "updated")]
            }))
        ]);
        await harness.Open().SaveAsync(session);

        var reloaded = (await harness.Open().GetAsync(session.SessionId)).ShouldNotBeNull();

        var (completed, unknown) = RecoveryEvidence.Collect(reloaded.History);
        completed.Select(call => call.ToolCallId).ShouldBe(new[] { "call-paired", "call-unpaired", "call-blocking" });
        completed.Select(call => call.Arguments).ShouldBe(new[] { "{\"id\":1}", "{\"id\":2}", "{\"id\":3}" });
        unknown.ShouldBeEmpty();
    }

    private static GatewaySession NewSession(string id)
        => new() { SessionId = SessionId.From(id), AgentId = AgentId.From("lease-agent"), Status = SessionStatus.Active };

    private static (GatewaySession Session, RunLease Lease) NewLeasedSession(string id, string inputContent)
    {
        var session = NewSession(id);
        var input = new SessionEntry { Role = MessageRole.User, Content = inputContent };
        session.AddEntry(input);
        var lease = RunLease.Admit(InputRef.For(input));
        session.AddEntry(Sentinel(lease));
        return (session, lease);
    }

    private static SessionEntry Sentinel(RunLease lease)
        => new() { Role = MessageRole.System, Content = "[agent turn in progress]", IsCrashSentinel = true, RunLease = lease };

    // A store whose Open() always returns an instance that shares only persisted state with the others.
    private abstract class StoreHarness : IDisposable
    {
        public static StoreHarness Create(string kind) => kind switch
        {
            "in-memory" => new InMemoryHarness(),
            "file" => new FileHarness(),
            "sqlite" => new SqliteHarness(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        public abstract ISessionStore Open();

        public virtual void Dispose()
        {
        }
    }

    private sealed class InMemoryHarness : StoreHarness
    {
        private readonly InMemorySessionStore _store = new();

        public override ISessionStore Open() => _store;
    }

    private sealed class FileHarness : StoreHarness
    {
        private readonly MockFileSystem _fileSystem = new();
        private readonly InMemoryConversationStore _conversations = new();
        private readonly string _storePath = Path.Combine(Path.GetTempPath(), "RunLeaseStoreRoundTripTests", Guid.NewGuid().ToString("N"));

        public FileHarness() => _fileSystem.Directory.CreateDirectory(_storePath);

        public override ISessionStore Open()
            => new FileSessionStore(_storePath, NullLogger<FileSessionStore>.Instance, _fileSystem, _conversations);
    }

    private sealed class SqliteHarness : StoreHarness
    {
        private readonly InMemoryConversationStore _conversations = new();
        private readonly string _directoryPath = Path.Combine(AppContext.BaseDirectory, "RunLeaseStoreRoundTripTests", Guid.NewGuid().ToString("N"));

        public SqliteHarness() => Directory.CreateDirectory(_directoryPath);

        public override ISessionStore Open()
            => new SqliteSessionStore(
                $"Data Source={Path.Combine(_directoryPath, "sessions.db")};Pooling=False",
                NullLogger<SqliteSessionStore>.Instance,
                _conversations);

        public override void Dispose()
        {
            if (Directory.Exists(_directoryPath))
                Directory.Delete(_directoryPath, recursive: true);
        }
    }
}
