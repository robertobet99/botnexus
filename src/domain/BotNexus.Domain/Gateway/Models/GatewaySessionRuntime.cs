using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Infrastructure runtime wrapper for <see cref="Session"/> that provides thread-safe mutation
/// and replay-buffer behavior used by gateway real-time delivery paths.
/// </summary>
public sealed class GatewaySessionRuntime
{
    private readonly Lock _lock = new();

    // Optimistic-concurrency counters for the compaction-rebase protocol (#532).
    // _additionVersion increments on append-only mutations (AddEntry/AddEntries).
    // _destructiveVersion increments on any mutation that removes or replaces
    // entries (ReplaceHistory, RemoveCrashSentinels with effect,
    // TryReplaceHistoryFromSnapshot with Applied/Rebased outcomes).
    // Compactors capture (snapshot, destructiveVersion, count) atomically, do
    // their slow LLM work, then re-acquire the lock to apply: if the
    // destructive version is unchanged they can either fast-path (counts
    // match) or rebase (count grew). If destructive changed they must abort.
    private long _additionVersion;
    private long _destructiveVersion;

    // Persistence cursor for aggregate stores. The cursor is guarded by the same lock as
    // history mutation, so a save can capture an immutable delta and acknowledge exactly the
    // prefix it wrote without hiding additions that arrive while I/O is in flight (#3907).
    private int _persistedHistoryCount;
    private long _persistedDestructiveVersion;
    private long _nextTransientPersistenceId = -1;
    private readonly HashSet<long> _observedPersistedHistoryIds = [];
    private readonly HashSet<string> _ownedPersistenceKeys = new(StringComparer.Ordinal);

    public GatewaySessionRuntime(Session session)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        StreamReplay = new SessionStreamReplay(new SessionReplayBuffer(), session);
    }

    /// <summary>
    /// Gets the session.
    /// </summary>
    public Session Session { get; }

    /// <summary>
    /// Gets the dedicated outbound-stream reconnect-replay peer for this
    /// session. The 8 forwarding stream-replay methods previously hosted here
    /// moved to <see cref="SessionStreamReplay"/> (#575); production and test
    /// callers must funnel through <c>session.StreamReplay</c>. Channels own
    /// the actual transport; this peer is only the in-memory sequenced log.
    /// </summary>
    public SessionStreamReplay StreamReplay { get; }

    /// <summary>
    /// Executes add entry.
    /// </summary>
    /// <param name="entry">The entry.</param>
    public void AddEntry(SessionEntry entry)
    {
        lock (_lock)
        {
            if (IsRetainedToolResultDuplicate(entry)) return;
            Session.History.Add(entry);
            _additionVersion++;
            Session.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Executes add entries.
    /// </summary>
    /// <param name="entries">The entries.</param>
    public void AddEntries(IEnumerable<SessionEntry> entries)
    {
        lock (_lock)
        {
            foreach (var entry in entries)
                if (!IsRetainedToolResultDuplicate(entry)) Session.History.Add(entry);
            _additionVersion++;
            Session.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private bool IsRetainedToolResultDuplicate(SessionEntry entry)
        => entry.Kind == MessageKind.ToolResult && entry.PersistenceKey is { } key
            && key.StartsWith("tool-result:", StringComparison.Ordinal)
            && Session.History.Any(existing => existing.PersistenceKey == key
                && existing.AgentRunId == entry.AgentRunId);

    /// <summary>
    /// Executes replace history.
    /// </summary>
    /// <param name="compactedEntries">The compacted entries.</param>
    public void ReplaceHistory(IReadOnlyList<SessionEntry> compactedEntries)
    {
        lock (_lock)
        {
            Session.History.Clear();
            Session.History.AddRange(compactedEntries);
            _destructiveVersion++;
            Session.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Removes the crash-sentinel entries of runs that have ended or died from the session history.
    /// Called on clean turn completion to ensure the sentinel does not persist
    /// as a real history entry after a successful run (#363), and by the write-time self-heal of orphans.
    /// </summary>
    /// <remarks>
    /// A pending reservation is the one sentinel this leaves alone. It is not the marker of a run that
    /// ended or died: it is a replacement that recovery has queued and that has not started yet, possibly
    /// behind another message for the same session. Removing it would cancel that replacement, so it is
    /// only ever consumed by its identity: adopted by the replacement it was reserved for, or taken back
    /// by recovery.
    /// <para>
    /// Every other sentinel goes, whichever run wrote it, and that is deliberate. Removal by the completing
    /// run's own lease alone would leave the sentinels of earlier failed or interrupted runs behind once a
    /// later run succeeds, so the next startup would report and replay inputs the session has already
    /// moved past. That would make the separate follow-up-sentinel defect, which is out of scope for
    /// recovery, worse. Consuming one sentinel by its identity is <see cref="TryRemoveCrashSentinel"/>.
    /// </para>
    /// </remarks>
    public void RemoveCrashSentinels()
    {
        lock (_lock)
        {
            // Only bump the destructive version when removal actually had effect.
            // A no-op scrub must not force concurrent compactions to abort.
            var removed = Session.History.RemoveAll(IsRemovableCrashSentinel);
            if (removed > 0)
                _destructiveVersion++;
        }
    }

    /// <summary>
    /// True when the history holds a crash sentinel that <see cref="RemoveCrashSentinels"/> would remove,
    /// that is, one that is not a pending reservation.
    /// </summary>
    /// <returns><see langword="true"/> when a scrub of the sentinels would change the history.</returns>
    public bool HasRemovableCrashSentinel()
    {
        lock (_lock)
            return Session.History.Exists(IsRemovableCrashSentinel);
    }

    private static bool IsRemovableCrashSentinel(SessionEntry entry)
        => entry.IsCrashSentinel && entry.RunLease is not { State: RunLeaseState.Pending };

    /// <summary>
    /// Replaces the run lease on the crash sentinel that carries <paramref name="leaseId"/>, in place.
    /// The change is applied under the history lock and is a destructive mutation: the row already exists
    /// in storage, so the next save must reconcile it rather than append, and a concurrent compaction that
    /// snapshotted the old row must abort rather than write it back.
    /// </summary>
    /// <param name="leaseId">The lease to update. Only a crash sentinel carrying this exact id is touched.</param>
    /// <param name="update">Computes the replacement from the lease as it is under the lock.</param>
    /// <returns><see langword="true"/> when the lease was found and replaced.</returns>
    public bool TryUpdateRunLease(RunLeaseId leaseId, Func<RunLease, RunLease> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_lock)
        {
            for (var index = 0; index < Session.History.Count; index++)
            {
                var entry = Session.History[index];
                if (!entry.IsCrashSentinel || entry.RunLease is not { } lease || lease.LeaseId != leaseId)
                    continue;

                var replacement = update(lease);
                if (ReferenceEquals(replacement, lease))
                    return true;

                Session.History[index] = entry with { RunLease = replacement };
                _destructiveVersion++;
                Session.UpdatedAt = DateTimeOffset.UtcNow;
                return true;
            }

            return false;
        }
    }

    /// <summary>Finds the run lease with <paramref name="leaseId"/> on any crash sentinel in the history.</summary>
    /// <param name="leaseId">The lease to find.</param>
    /// <returns>The lease as it is now, or <see langword="null"/> when no sentinel carries it.</returns>
    public RunLease? FindRunLease(RunLeaseId leaseId)
    {
        lock (_lock)
        {
            foreach (var entry in Session.History)
            {
                if (entry.IsCrashSentinel && entry.RunLease is { } lease && lease.LeaseId == leaseId)
                    return lease;
            }

            return null;
        }
    }

    /// <summary>
    /// Removes the one crash sentinel whose lease is <paramref name="leaseId"/> and no other. Like every
    /// removal of a stored row this is a destructive mutation, so the next save reconciles it.
    /// </summary>
    /// <param name="leaseId">The lease whose sentinel is consumed.</param>
    /// <param name="removed">The sentinel that was removed, so a failed save can put it back.</param>
    /// <returns><see langword="true"/> when a sentinel carried the lease and was removed.</returns>
    public bool TryRemoveCrashSentinel(RunLeaseId leaseId, out SessionEntry? removed)
    {
        lock (_lock)
        {
            var index = Session.History.FindIndex(entry =>
                entry.IsCrashSentinel && entry.RunLease is { } lease && lease.LeaseId == leaseId);
            if (index < 0)
            {
                removed = null;
                return false;
            }

            removed = Session.History[index];
            Session.History.RemoveAt(index);
            _destructiveVersion++;
            Session.UpdatedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    /// <summary>
    /// Removes exactly the stored entry instance <paramref name="stored"/>, matched by reference. It takes
    /// back a row the caller appended, whether its save did not persist or a later save retracts it, without
    /// touching anything another writer added meanwhile.
    /// </summary>
    /// <param name="stored">The instance held in the history, as returned by an append.</param>
    /// <returns><see langword="true"/> when that instance was in the history and was removed.</returns>
    public bool TryRemoveEntry(SessionEntry stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        lock (_lock)
        {
            var index = Session.History.FindIndex(entry => ReferenceEquals(entry, stored));
            if (index < 0)
                return false;

            Session.History.RemoveAt(index);
            _destructiveVersion++;
            Session.UpdatedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    /// <summary>
    /// Atomically appends <paramref name="entry"/> to the history AND returns
    /// an immutable snapshot of the resulting history together with the
    /// destructive-mutation version observed at snapshot time. Use this when
    /// a subsequent locked operation (such as heartbeat ack-prune) must
    /// identify the just-appended entry by its position — calling
    /// <see cref="AddEntry"/> followed by <see cref="SnapshotHistoryForCompaction"/>
    /// as separate locked operations leaves a window where a concurrent
    /// destructive mutation can shift the appended entry away from
    /// <c>snapshot.Count - 1</c>. Also captures the
    /// <see cref="Session.UpdatedAt"/> value observed immediately before the
    /// append in <see cref="SessionAppendResult.PriorUpdatedAt"/>, so callers
    /// that intend to restore UpdatedAt after a slow follow-on operation
    /// don't have to do an unsafe pre-call read.
    /// </summary>
    /// <param name="entry">The entry to append.</param>
    /// <returns>
    /// The result of the append. <see cref="SessionAppendResult.Snapshot"/>
    /// is taken under the same lock as the append (the appended entry is
    /// always at index <c>snapshot.Count - 1</c>);
    /// <see cref="SessionAppendResult.PriorUpdatedAt"/> is the
    /// <see cref="Session.UpdatedAt"/> value sampled immediately before the
    /// append, also under the same lock.
    /// </returns>
    public SessionAppendResult AddEntryAndSnapshot(SessionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            // Capture the pre-append UpdatedAt under the same lock as the
            // append (rubber-duck bug-hunt BLOCKING for #573). A callsite
            // that reads session.UpdatedAt OUTSIDE the lock before calling
            // AddEntryAndSnapshot can be raced by a concurrent destructive
            // mutation that lands between the read and the lock, leaving
            // the caller with a stale anchor that — if later passed to
            // TryReplaceHistoryFromSnapshot(restoreUpdatedAtOnApplied: ...)
            // on the Applied path — would roll the timestamp backwards
            // past the concurrent mutation.
            var priorUpdatedAt = Session.UpdatedAt;
            Session.History.Add(entry);
            _additionVersion++;
            Session.UpdatedAt = DateTimeOffset.UtcNow;
            var snapshot = Session.History.ToArray();
            return new SessionAppendResult(
                new HistorySnapshot(snapshot, _destructiveVersion, snapshot.Length),
                priorUpdatedAt);
        }
    }

    /// <summary>
    /// Copies aggregate and persistence state under the history lock. The candidate owns its
    /// history entries and cursor, so a speculative rewrite or persistence acknowledgement
    /// cannot leak into a cached session retained by reset or transcript readers.
    /// </summary>
    internal GatewaySessionRuntime CreateDetachedCopy()
    {
        lock (_lock)
        {
            var copy = new GatewaySessionRuntime(Session with
            {
                History = Session.History.Select(static entry => entry with { }).ToList(),
                Metadata = new Dictionary<string, object?>(Session.Metadata)
            })
            {
                _additionVersion = _additionVersion,
                _destructiveVersion = _destructiveVersion,
                _persistedHistoryCount = _persistedHistoryCount,
                _persistedDestructiveVersion = _persistedDestructiveVersion,
                _nextTransientPersistenceId = _nextTransientPersistenceId
            };
            copy._observedPersistedHistoryIds.UnionWith(_observedPersistedHistoryIds);
            copy._ownedPersistenceKeys.UnionWith(_ownedPersistenceKeys);
            copy.StreamReplay.SetState(this.StreamReplay.NextSequenceId, this.StreamReplay.GetEventSnapshot());
            return copy;
        }
    }

    /// <summary>
    /// Atomically captures (a) an immutable snapshot of the current history,
    /// (b) the destructive-mutation version observed at snapshot time, and
    /// (c) the snapshot length. Compactors operate on the returned snapshot —
    /// not on <see cref="Session"/>.<c>History</c> — and pass the captured
    /// version+count back to <see cref="TryReplaceHistoryFromSnapshot"/> so the
    /// runtime can detect concurrent mutations and pick the safe apply path.
    /// </summary>
    public HistorySnapshot SnapshotHistoryForCompaction()
    {
        lock (_lock)
        {
            // Defensive copy: callers must not be able to mutate live history through
            // the returned list, and additions made after this call must not be
            // observable through the snapshot itself (only via Count/_additionVersion).
            var snapshot = Session.History.ToArray();
            return new HistorySnapshot(snapshot, _destructiveVersion, snapshot.Length);
        }
    }

    /// <summary>
    /// Optimistically replaces the history with <paramref name="replacement"/>,
    /// gated on the snapshot's destructive-version + count being current.
    /// See <see cref="HistoryReplaceOutcome"/> for the three possible outcomes:
    /// fast-path apply, rebased apply (concurrent additions appended), or
    /// aborted (concurrent destructive mutation detected — live history
    /// unchanged).
    /// </summary>
    /// <param name="replacement">The replacement history derived from the snapshot.</param>
    /// <param name="expectedDestructiveVersion">Destructive-mutation version observed when the snapshot was taken.</param>
    /// <param name="expectedHistoryCount">Snapshot count.</param>
    /// <param name="restoreUpdatedAtOnApplied">
    /// When non-null AND the apply takes the fast Applied path (no concurrent
    /// activity), the session's <c>UpdatedAt</c> is restored to this value
    /// INSIDE the same runtime lock as the apply. The lock is the critical
    /// invariant: a separate post-apply write to <c>UpdatedAt</c> would race
    /// with concurrent <see cref="AddEntry"/> calls and clobber legitimate
    /// fresh activity timestamps. On Rebased the parameter is ignored (the
    /// concurrent activity legitimately bumped <c>UpdatedAt</c>); on Aborted
    /// no write occurs.
    /// </param>
    public HistoryReplaceOutcome TryReplaceHistoryFromSnapshot(
        IReadOnlyList<SessionEntry> replacement,
        long expectedDestructiveVersion,
        int expectedHistoryCount,
        DateTimeOffset? restoreUpdatedAtOnApplied = null)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (expectedHistoryCount < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedHistoryCount), "Snapshot count cannot be negative.");

        lock (_lock)
        {
            if (_destructiveVersion != expectedDestructiveVersion)
                return HistoryReplaceOutcome.Aborted;

            var currentCount = Session.History.Count;
            if (currentCount == expectedHistoryCount)
            {
                Session.History.Clear();
                Session.History.AddRange(replacement);
                _destructiveVersion++;
                // Restore caller-supplied UpdatedAt under the lock when supplied;
                // otherwise stamp the apply time. The lock is the invariant —
                // a separate post-apply write would race with concurrent AddEntry.
                Session.UpdatedAt = restoreUpdatedAtOnApplied ?? DateTimeOffset.UtcNow;
                return HistoryReplaceOutcome.Applied;
            }

            // Destructive version unchanged but count grew — only AddEntry/AddEntries
            // ran during the work window. Append the concurrent tail after the
            // replacement so additions are preserved.
            if (currentCount > expectedHistoryCount)
            {
                var concurrentTail = new SessionEntry[currentCount - expectedHistoryCount];
                Session.History.CopyTo(expectedHistoryCount, concurrentTail, 0, concurrentTail.Length);
                Session.History.Clear();
                Session.History.AddRange(replacement);
                Session.History.AddRange(concurrentTail);
                _destructiveVersion++;
                // Rebased: concurrent activity is real; do NOT honor
                // restoreUpdatedAtOnApplied — restoring to a stale pre-apply
                // value would lie about activity timing.
                Session.UpdatedAt = DateTimeOffset.UtcNow;
                return HistoryReplaceOutcome.Rebased;
            }

            // currentCount < expectedHistoryCount with destructive version unchanged
            // is unreachable in current code paths (only RemoveCrashSentinels and
            // ReplaceHistory shrink, both of which bump _destructiveVersion when
            // they have effect). Treat as a conflict for safety.
            return HistoryReplaceOutcome.Aborted;
        }
    }

    /// <summary>
    /// Executes get history snapshot.
    /// </summary>
    /// <returns>The get history snapshot result.</returns>
    public IReadOnlyList<SessionEntry> GetHistorySnapshot()
    {
        lock (_lock)
        {
            return Session.History.ToList();
        }
    }

    /// <summary>
    /// Captures the history work required by an aggregate save. Ordinary append-only mutation
    /// returns only the unpersisted tail. A destructive mutation returns the complete current
    /// history and requests explicit replacement of the persisted rows.
    /// </summary>
    public SessionHistoryPersistenceSnapshot CaptureHistoryForPersistence()
    {
        lock (_lock)
        {
            var requiresReplacement = _destructiveVersion != _persistedDestructiveVersion
                || _persistedHistoryCount > Session.History.Count;
            var startIndex = requiresReplacement ? 0 : _persistedHistoryCount;
            var entries = Session.History.GetRange(startIndex, Session.History.Count - startIndex).ToArray();
            foreach (var entry in entries)
            {
                entry.PersistenceId ??= _nextTransientPersistenceId--;
                if (entry.PersistenceId < 0)
                {
                    entry.PersistenceKey ??= Guid.NewGuid().ToString("N");
                    _ownedPersistenceKeys.Add(entry.PersistenceKey);
                }
            }

            IReadOnlyList<long> removedPersistedIds = [];
            IReadOnlyList<string> removedPersistenceKeys = [];
            if (requiresReplacement)
            {
                var retainedPersistedIds = Session.History
                    .Select(static entry => entry.PersistenceId)
                    .OfType<long>()
                    .Where(static id => id > 0)
                    .ToHashSet();
                removedPersistedIds = _observedPersistedHistoryIds
                    .Where(id => !retainedPersistedIds.Contains(id))
                    .ToArray();
                var retainedPersistenceKeys = Session.History
                    .Select(static entry => entry.PersistenceKey)
                    .OfType<string>()
                    .ToHashSet(StringComparer.Ordinal);
                removedPersistenceKeys = _ownedPersistenceKeys
                    .Where(key => !retainedPersistenceKeys.Contains(key))
                    .ToArray();
            }
            return new SessionHistoryPersistenceSnapshot(
                entries,
                removedPersistedIds,
                removedPersistenceKeys,
                requiresReplacement,
                startIndex,
                Session.History.Count,
                _destructiveVersion);
        }
    }

    /// <summary>
    /// Marks only the prefix represented by <paramref name="snapshot"/> as durable. Concurrent
    /// appends remain beyond the cursor for the next save. If history was destructively changed
    /// during the write, the acknowledgement is ignored so the next save reconciles by replacement.
    /// </summary>
    public void AcknowledgeHistoryPersistence(
        SessionHistoryPersistenceSnapshot snapshot,
        IReadOnlyDictionary<long, long> insertedRowIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(insertedRowIds);
        lock (_lock)
        {
            // The captured entries are the live objects on the ordinary append path, so applying
            // returned row ids costs O(delta). Only a destructive mutation racing the write can
            // replace them with `with` clones; that exceptional path scans the current history to
            // transfer the durable ids before leaving reconciliation pending.
            foreach (var entry in snapshot.Entries)
            {
                if (entry.PersistenceId is { } transientId
                    && insertedRowIds.TryGetValue(transientId, out var durableId))
                {
                    entry.PersistenceId = durableId;
                }
            }

            if (_destructiveVersion != snapshot.DestructiveVersion)
            {
                foreach (var entry in Session.History)
                {
                    if (entry.PersistenceId is { } transientId
                        && insertedRowIds.TryGetValue(transientId, out var durableId))
                    {
                        entry.PersistenceId = durableId;
                    }
                }
            }

            foreach (var removedId in snapshot.RemovedPersistedIds)
                _observedPersistedHistoryIds.Remove(removedId);
            foreach (var removedKey in snapshot.RemovedPersistenceKeys)
                _ownedPersistenceKeys.Remove(removedKey);
            foreach (var durableId in insertedRowIds.Values)
                _observedPersistedHistoryIds.Add(durableId);

            if (_destructiveVersion != snapshot.DestructiveVersion)
                return;

            if (!snapshot.RequiresReplacement && _persistedHistoryCount != snapshot.StartIndex)
                return;

            _persistedHistoryCount = snapshot.EndIndex;
            _persistedDestructiveVersion = snapshot.DestructiveVersion;
        }
    }

    /// <summary>
    /// Initializes a hydrated session's cursor after its complete history has been read from the
    /// store. New sessions deliberately do not call this method, so their initial history is pending.
    /// </summary>
    public void MarkCurrentHistoryPersisted()
    {
        lock (_lock)
        {
            _persistedHistoryCount = Session.History.Count;
            _persistedDestructiveVersion = _destructiveVersion;
            _observedPersistedHistoryIds.Clear();
            _ownedPersistenceKeys.Clear();
            foreach (var id in Session.History
                         .Select(static entry => entry.PersistenceId)
                         .OfType<long>()
                         .Where(static id => id > 0))
            {
                _observedPersistedHistoryIds.Add(id);
            }
            foreach (var key in Session.History
                         .Select(static entry => entry.PersistenceKey)
                         .OfType<string>())
            {
                _ownedPersistenceKeys.Add(key);
            }
        }
    }

    /// <summary>
    /// Executes get history snapshot.
    /// </summary>
    /// <param name="offset">The offset.</param>
    /// <param name="limit">The limit.</param>
    /// <returns>The get history snapshot result.</returns>
    public IReadOnlyList<SessionEntry> GetHistorySnapshot(int offset, int limit)
    {
        lock (_lock)
        {
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be greater than or equal to zero.");
            if (limit < 0)
                throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than or equal to zero.");

            return Session.History
                .Skip(offset)
                .Take(limit)
                .ToList();
        }
    }

}

/// <summary>
/// Immutable history work item captured for one persistence attempt. Entries have stable positive
/// durable identities or negative transient identities assigned under the history mutation lock.
/// Removed identities contain only rows this aggregate previously observed and explicitly removed;
/// rows concurrently appended through another store path are outside its deletion authority.
/// </summary>
public sealed record SessionHistoryPersistenceSnapshot(
    IReadOnlyList<SessionEntry> Entries,
    IReadOnlyList<long> RemovedPersistedIds,
    IReadOnlyList<string> RemovedPersistenceKeys,
    bool RequiresReplacement,
    int StartIndex,
    int EndIndex,
    long DestructiveVersion);
