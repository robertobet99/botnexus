using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Domain.Tests;

public sealed class SessionRunLeaseTests
{
    private static readonly DateTimeOffset Recorded = new(2026, 11, 8, 23, 1, 31, TimeSpan.Zero);

    [Fact]
    public void TryUpdateRunLease_ReplacesOnlyTheSentinelCarryingThatId()
    {
        var session = NewSession();
        var first = Sentinel("First message.");
        var second = Sentinel("Second message.");
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "First message." });
        session.AddEntry(first);
        session.AddEntry(second);

        var updated = session.TryUpdateRunLease(first.RunLease.ShouldNotBeNull().LeaseId, lease => lease.MarkFailed(Recorded, "boom", null));

        updated.ShouldBeTrue();
        var history = session.GetHistorySnapshot();
        history[1].RunLease.ShouldNotBeNull().State.ShouldBe(RunLeaseState.Failed);
        history[2].ShouldBeSameAs(second, "another run's sentinel must not be touched");
        history[2].RunLease.ShouldNotBeNull().State.ShouldBe(RunLeaseState.Running);
        history[0].RunLease.ShouldBeNull();
    }

    [Fact]
    public void TryUpdateRunLease_ReturnsFalseForAnUnknownLease_AndChangesNothing()
    {
        var session = NewSession();
        session.AddEntry(Sentinel("First message."));
        var before = session.GetHistorySnapshot();

        var updated = session.TryUpdateRunLease(RunLeaseId.Create(), lease => lease.MarkFailed(Recorded, "boom", null));

        updated.ShouldBeFalse();
        session.GetHistorySnapshot().ShouldBe(before);
    }

    [Fact]
    public void TryUpdateRunLease_IgnoresALeaseOnAnEntryThatIsNotACrashSentinel()
    {
        var session = NewSession();
        var lease = RunLease.Admit(InputRef.For(new SessionEntry { Role = MessageRole.User, Content = "Hello." }));
        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "Not a sentinel.", RunLease = lease });

        session.TryUpdateRunLease(lease.LeaseId, current => current.MarkFailed(Recorded, "boom", null)).ShouldBeFalse();

        session.GetHistorySnapshot().Single().RunLease.ShouldBe(lease);
    }

    [Fact]
    public void TryUpdateRunLease_KeepsTheRowsPersistenceIdentity()
    {
        var session = NewSession();
        var sentinel = Sentinel("First message.");
        sentinel.PersistenceId = 42;
        sentinel.PersistenceKey = "key-42";
        session.AddEntry(sentinel);

        session.TryUpdateRunLease(sentinel.RunLease.ShouldNotBeNull().LeaseId, lease => lease.MarkFailed(Recorded, "boom", null)).ShouldBeTrue();

        var replaced = session.GetHistorySnapshot().Single();
        replaced.PersistenceId.ShouldBe(42, "the update must reach the same stored row, not insert another");
        replaced.PersistenceKey.ShouldBe("key-42");
    }

    [Fact]
    public void TryUpdateRunLease_AfterAPersistedSave_RequiresAReconcilingSave()
    {
        var session = NewSession();
        var sentinel = Sentinel("First message.");
        session.AddEntry(sentinel);
        session.AcknowledgeHistoryPersistence(session.CaptureHistoryForPersistence(), new Dictionary<long, long>());
        session.CaptureHistoryForPersistence().RequiresReplacement.ShouldBeFalse("fixture: nothing is pending");

        session.TryUpdateRunLease(sentinel.RunLease.ShouldNotBeNull().LeaseId, lease => lease.MarkFailed(Recorded, "boom", null)).ShouldBeTrue();

        session.CaptureHistoryForPersistence().RequiresReplacement
            .ShouldBeTrue("an in-place change to a stored row can only be saved by reconciling it");
    }

    [Fact]
    public void TryUpdateRunLease_WhenTheUpdateChangesNothing_LeavesTheHistoryClean()
    {
        var session = NewSession();
        var sentinel = Sentinel("First message.");
        session.AddEntry(sentinel);
        session.AcknowledgeHistoryPersistence(session.CaptureHistoryForPersistence(), new Dictionary<long, long>());

        session.TryUpdateRunLease(sentinel.RunLease.ShouldNotBeNull().LeaseId, lease => lease).ShouldBeTrue();

        session.CaptureHistoryForPersistence().RequiresReplacement.ShouldBeFalse();
        session.GetHistorySnapshot().Single().ShouldBeSameAs(sentinel);
    }

    [Fact]
    public void FindRunLease_FindsTheLeaseByIdOnASentinelAndNowhereElse()
    {
        var session = NewSession();
        var sentinel = Sentinel("First message.");
        var other = RunLease.Admit(InputRef.For(new SessionEntry { Role = MessageRole.User, Content = "Hello." }));
        session.AddEntry(sentinel);
        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "Not a sentinel.", RunLease = other });

        session.FindRunLease(sentinel.RunLease.ShouldNotBeNull().LeaseId).ShouldBe(sentinel.RunLease);
        session.FindRunLease(other.LeaseId).ShouldBeNull("only a crash sentinel's lease counts");
        session.FindRunLease(RunLeaseId.Create()).ShouldBeNull();
    }

    [Fact]
    public void TryRemoveCrashSentinel_RemovesTheOneSentinelWithThatLease_AndHandsItBack()
    {
        var session = NewSession();
        var first = Sentinel("First message.");
        var second = Sentinel("Second message.");
        session.AddEntry(first);
        session.AddEntry(second);

        session.TryRemoveCrashSentinel(first.RunLease.ShouldNotBeNull().LeaseId, out var removed).ShouldBeTrue();

        removed.ShouldBeSameAs(first);
        session.GetHistorySnapshot().Single().ShouldBeSameAs(second, "another run's sentinel must not be touched");
    }

    [Fact]
    public void TryRemoveCrashSentinel_ForAnUnknownLease_ChangesNothing()
    {
        var session = NewSession();
        session.AddEntry(Sentinel("First message."));
        var before = session.GetHistorySnapshot();

        session.TryRemoveCrashSentinel(RunLeaseId.Create(), out var removed).ShouldBeFalse();

        removed.ShouldBeNull();
        session.GetHistorySnapshot().ShouldBe(before);
    }

    [Fact]
    public void TryRemoveCrashSentinel_AfterAPersistedSave_RequiresAReconcilingSave()
    {
        var session = NewSession();
        var sentinel = Sentinel("First message.");
        session.AddEntry(sentinel);
        session.AcknowledgeHistoryPersistence(session.CaptureHistoryForPersistence(), new Dictionary<long, long>());

        session.TryRemoveCrashSentinel(sentinel.RunLease.ShouldNotBeNull().LeaseId, out _).ShouldBeTrue();

        session.CaptureHistoryForPersistence().RequiresReplacement.ShouldBeTrue("a removed stored row can only be saved by reconciling");
    }

    // Proves: the scrub clears every sentinel of a run that ended or died (one without a lease, a running one, a failed one) and leaves a pending reservation, which belongs to a replacement that has not started.
    [Fact]
    public void RemoveCrashSentinels_ClearsEverySentinelExceptAPendingReservation()
    {
        var session = NewSession();
        var reservation = Reservation("Queued.");
        var failed = Sentinel("Failed.");
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "Hello." });
        session.AddEntry(Sentinel("Running."));
        session.AddEntry(reservation);
        session.AddEntry(failed with { RunLease = failed.RunLease.ShouldNotBeNull().MarkFailed(Recorded, "boom", null) });
        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "[legacy sentinel]", IsCrashSentinel = true });

        session.RemoveCrashSentinels();

        var history = session.GetHistorySnapshot();
        history.Where(entry => entry.IsCrashSentinel).ShouldHaveSingleItem().ShouldBeSameAs(reservation);
        history.ShouldContain(entry => entry.Content == "Hello.");
    }

    // Proves: a scrub that finds only a pending reservation changes nothing, so it neither forces a reconciling save nor aborts a concurrent compaction.
    [Fact]
    public void RemoveCrashSentinels_WithOnlyAPendingReservation_LeavesTheHistoryClean()
    {
        var session = NewSession();
        var reservation = Reservation("Queued.");
        session.AddEntry(reservation);
        session.AcknowledgeHistoryPersistence(session.CaptureHistoryForPersistence(), new Dictionary<long, long>());

        session.RemoveCrashSentinels();

        session.GetHistorySnapshot().Single().ShouldBeSameAs(reservation);
        session.CaptureHistoryForPersistence().RequiresReplacement.ShouldBeFalse();
    }

    // Proves: a session reports a sentinel to scrub unless the only sentinel is a pending reservation.
    [Fact]
    public void HasRemovableCrashSentinel_IsFalseForAPendingReservationAlone_AndTrueForAnyOtherSentinel()
    {
        var session = NewSession();
        session.HasRemovableCrashSentinel().ShouldBeFalse("an empty history has nothing to scrub");

        session.AddEntry(Reservation("Queued."));
        session.HasRemovableCrashSentinel().ShouldBeFalse("a pending reservation is not scrubbed");

        session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "[legacy sentinel]", IsCrashSentinel = true });
        session.HasRemovableCrashSentinel().ShouldBeTrue("a sentinel without a lease is an ordinary orphan");
    }

    [Fact]
    public void TryRemoveEntry_RemovesExactlyThatInstance_NotAnEqualOne()
    {
        var session = NewSession();
        var appended = session.AddEntryAndSnapshot(new SessionEntry { Role = MessageRole.Notification, Content = "Status.", Timestamp = Recorded })
            .Snapshot.Entries[^1];
        session.AddEntry(new SessionEntry { Role = MessageRole.Notification, Content = "Status.", Timestamp = Recorded });

        session.TryRemoveEntry(appended).ShouldBeTrue();

        var history = session.GetHistorySnapshot();
        history.Count.ShouldBe(1, "the equal row another writer added is not the one removed");
        history.ShouldNotContain(entry => ReferenceEquals(entry, appended));
        session.TryRemoveEntry(appended).ShouldBeFalse("it is already gone");
    }

    [Fact]
    public void RemoveCrashSentinels_RemovesLeasedSentinels()
    {
        var session = NewSession();
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "First message." });
        session.AddEntry(Sentinel("First message."));

        session.RemoveCrashSentinels();

        session.GetHistorySnapshot().ShouldNotContain(entry => entry.IsCrashSentinel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionEntry_Json_OmitsALeaseWhenThereIsNone(bool camelCase)
    {
        var options = camelCase ? new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase } : new JsonSerializerOptions();

        var json = JsonSerializer.Serialize(new SessionEntry { Role = MessageRole.User, Content = "Hello." }, options);

        json.ToLowerInvariant().ShouldNotContain("runlease");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionEntry_Json_RoundTripsALeaseOnASentinel(bool camelCase)
    {
        var options = camelCase ? new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase } : new JsonSerializerOptions();
        var original = Sentinel("First message.");

        var json = JsonSerializer.Serialize(original, options);
        var readBack = JsonSerializer.Deserialize<SessionEntry>(json, options).ShouldNotBeNull();

        readBack.IsCrashSentinel.ShouldBeTrue();
        readBack.RunLease.ShouldBe(original.RunLease);
    }

    [Fact]
    public void SessionEntry_Json_ReadsARowWrittenBeforeLeasesExistedAsLeaseless()
    {
        const string legacyRow = "{\"Role\":\"system\",\"Content\":\"[agent turn in progress]\",\"IsCrashSentinel\":true}";

        var entry = JsonSerializer.Deserialize<SessionEntry>(legacyRow).ShouldNotBeNull();

        entry.IsCrashSentinel.ShouldBeTrue();
        entry.RunLease.ShouldBeNull();
    }

    private static GatewaySession NewSession()
        => new() { SessionId = SessionId.From("lease-session"), AgentId = AgentId.From("lease-agent") };

    private static SessionEntry Sentinel(string inputContent)
        => new()
        {
            Role = MessageRole.System,
            Content = "[agent turn in progress]",
            IsCrashSentinel = true,
            RunLease = RunLease.Admit(InputRef.For(new SessionEntry { Role = MessageRole.User, Content = inputContent, Timestamp = Recorded }))
        };

    // The sentinel of a replacement that recovery has queued and that has not started.
    private static SessionEntry Reservation(string inputContent)
    {
        var running = Sentinel(inputContent);
        return running with { RunLease = running.RunLease.ShouldNotBeNull().QueueSuccessor("instance-a") };
    }
}
