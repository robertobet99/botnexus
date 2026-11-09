using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// The one place a <see cref="RunLease"/> becomes the text stored in <c>session_history.run_lease_json</c>
/// and back, so the insert, the update and the reader cannot disagree about the shape.
/// </summary>
internal static class SqliteRunLeaseJson
{
    /// <summary>The stored document for a lease, or <see langword="null"/> for an entry with none.</summary>
    internal static string? Serialize(RunLease? lease)
        => lease is null ? null : JsonSerializer.Serialize(lease, SqliteSessionStore.JsonOptions);

    /// <summary>
    /// Reads a stored lease. A document that cannot be read is treated as no lease: recovery then falls back
    /// to what it does for a sentinel written before leases existed, which never invents an input, whereas
    /// letting one corrupt cell make a whole session unreadable would be a far worse failure.
    /// </summary>
    internal static RunLease? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<RunLease>(json, SqliteSessionStore.JsonOptions);
        }
        catch (Exception)
        {
            // JsonException for a malformed document, and whatever the identifier converters throw for a
            // blank or invalid id: every one of them means the same thing, "this is not a lease".
            return null;
        }
    }
}
