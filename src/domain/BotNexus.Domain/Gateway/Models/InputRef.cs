using System.Security.Cryptography;
using System.Text;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Names one stored transcript entry by what it is rather than by where a store keeps it: its role, the
/// exact instant it was recorded and a digest of its stored content.
/// </summary>
/// <remarks>
/// <para>
/// Why not a store identity. A row id or persistence key is a SQLite concept: the SQLite store assigns
/// them when it inserts a row, the file store does not persist either (both are <c>JsonIgnore</c>d), and
/// the in-memory store never assigns them. A reference built on them would resolve in one store and
/// silently fail in the others. Role, timestamp ticks and a content digest are written by all three stores
/// and read back identically, so one reference resolves everywhere.
/// </para>
/// <para>
/// A reference is not a promise of uniqueness, so resolution never guesses. <see cref="Resolve"/> reports
/// <see cref="InputRefMatch.Ambiguous"/> when more than one entry matches and
/// <see cref="InputRefMatch.None"/> when none does, and a caller treats both as "no initiating input". In
/// practice a collision needs two entries with the same role and content recorded in the same
/// 100-nanosecond tick, which only a store that coarsened timestamps could produce, and then the safe
/// answer is exactly the blocker.
/// </para>
/// </remarks>
/// <param name="Role">The entry's role when it was stored.</param>
/// <param name="Timestamp">
/// The instant the entry was recorded. Compared by UTC ticks, so a store that returns the same instant
/// with a different offset still matches.
/// </param>
/// <param name="ContentSha256">
/// Lowercase hex SHA-256 of the entry's stored content, that is the content after any secret redaction,
/// which is what every store reads back. The content itself is not repeated here.
/// </param>
public sealed record InputRef(MessageRole Role, DateTimeOffset Timestamp, string ContentSha256)
{
    /// <summary>Builds the reference for an entry exactly as it is stored.</summary>
    /// <param name="entry">The stored entry, which must be the instance held in the session history.</param>
    /// <returns>A reference that <see cref="Matches"/> the entry and any identical read-back of it.</returns>
    public static InputRef For(SessionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new InputRef(entry.Role, entry.Timestamp, DigestOf(entry.Content));
    }

    /// <summary>Whether <paramref name="entry"/> is the entry this reference names.</summary>
    /// <param name="entry">The candidate history entry.</param>
    /// <returns><see langword="true"/> when role, instant and content digest all agree.</returns>
    public bool Matches(SessionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Cheapest checks first: the digest is only computed for an entry that already agrees on role and instant.
        return entry.Role.Equals(Role)
            && entry.Timestamp.UtcTicks == Timestamp.UtcTicks
            && string.Equals(DigestOf(entry.Content), ContentSha256, StringComparison.Ordinal);
    }

    /// <summary>
    /// Finds the one entry this reference names. Folded (compacted) entries are still found, because
    /// compaction keeps the transcript and only hides rows from the model.
    /// </summary>
    /// <param name="history">The session history to search.</param>
    /// <returns>
    /// The single match, or a result saying there was none or that it was ambiguous. Never a guess.
    /// </returns>
    public InputRefResolution Resolve(IEnumerable<SessionEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        SessionEntry? found = null;
        foreach (var entry in history)
        {
            if (!Matches(entry))
                continue;

            if (found is not null)
                return new InputRefResolution(InputRefMatch.Ambiguous, null);

            found = entry;
        }

        return found is null
            ? new InputRefResolution(InputRefMatch.None, null)
            : new InputRefResolution(InputRefMatch.Single, found);
    }

    private static string DigestOf(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}

/// <summary>How many history entries an <see cref="InputRef"/> matched.</summary>
public enum InputRefMatch
{
    /// <summary>No entry matches: the input was removed, rewritten or never stored.</summary>
    None,

    /// <summary>Exactly one entry matches.</summary>
    Single,

    /// <summary>More than one entry matches, so the reference does not identify an input.</summary>
    Ambiguous
}

/// <summary>The outcome of resolving an <see cref="InputRef"/> against a session history.</summary>
/// <param name="Match">How many entries matched.</param>
/// <param name="Entry">The matched entry when <paramref name="Match"/> is <see cref="InputRefMatch.Single"/>; otherwise <see langword="null"/>.</param>
public readonly record struct InputRefResolution(InputRefMatch Match, SessionEntry? Entry);
