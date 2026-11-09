using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.TestInfrastructure;

/// <summary>
/// A directory that outlives the service graphs built over it, so a test can end one graph and start another
/// over the same files, which is what a process restart is. The conversation and session stores live in
/// SQLite files here, as production's default composition keeps them, and the synthetic effect's marker file
/// lives here too, so "the mutation ran exactly once" counts across graphs.
/// </summary>
/// <remarks>
/// <para>
/// The test owns the lifecycle: create it, hand it to each harness through
/// <see cref="RecoveryIngressOptions.Durable"/>, dispose it last. A graph that did not finish tearing down may
/// still be writing here, so disposal leaves the files in place while any graph that started over the disk
/// has not stopped cleanly. Nothing here ever touches a real data directory.
/// </para>
/// <para>
/// Both stores must be durable together. A session store that starts over a conversation store that does not
/// have the session's conversation removes the session at start-up as dangling, so a second graph over only
/// the session file would lose the very session it exists to recover.
/// </para>
/// </remarks>
internal sealed class DurableTestDisk : IDisposable
{
    private int _liveGraphs;

    /// <summary>Creates an empty disk in a unique temp directory.</summary>
    internal DurableTestDisk()
    {
        Root = Path.Combine(Path.GetTempPath(), "botnexus-recovery-durable", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>The unique directory every file of this disk lives in.</summary>
    internal string Root { get; }

    /// <summary>The connection string of the session store's file.</summary>
    internal string SessionsConnectionString => ConnectionString("sessions.db");

    /// <summary>The connection string of the conversation store's file.</summary>
    internal string ConversationsConnectionString => ConnectionString("conversations.db");

    /// <summary>The marker file the synthetic effect appends to, shared by every graph over this disk.</summary>
    internal string EffectsMarkerPath => Path.Combine(Root, "effects", "applied.log");

    /// <summary>
    /// Reads a session straight from the files through no service graph: what a process that started right
    /// now would find. The conversation store opens over its own file first, as a real start-up does.
    /// </summary>
    internal async Task<GatewaySession> ReadSessionAsync(SessionId sessionId)
    {
        var conversations = new SqliteConversationStore(ConversationsConnectionString, NullLogger<SqliteConversationStore>.Instance);
        var sessions = new SqliteSessionStore(SessionsConnectionString, NullLogger<SqliteSessionStore>.Instance, conversations);
        var session = await sessions.GetAsync(sessionId);
        return session.ShouldNotBeNull($"session {sessionId.Value} should be durable");
    }

    /// <summary>Called by a harness that started over this disk.</summary>
    internal void GraphStarted() => Interlocked.Increment(ref _liveGraphs);

    /// <summary>Called by a harness that finished tearing its graph down.</summary>
    internal void GraphStopped() => Interlocked.Decrement(ref _liveGraphs);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Volatile.Read(ref _liveGraphs) > 0)
            return;

        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a unique temp directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a unique temp directory.
        }
    }

    // Pooling is off so no connection outlives its use, which is what lets the files be deleted.
    private string ConnectionString(string file)
        => new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, file), Pooling = false }.ToString();
}
