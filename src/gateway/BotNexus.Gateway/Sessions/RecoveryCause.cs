namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Why interrupted-turn recovery is running. The cause decides what the reader is told, because a
/// restart and a failed run are different facts and only one of them is true at a time.
/// </summary>
internal enum RecoveryCause
{
    /// <summary>
    /// The gateway process went away mid-turn and the startup scan found the sentinel it left behind.
    /// The restart is a fact the scan can state before it knows whether a replacement is admitted.
    /// </summary>
    GatewayRestart,

    /// <summary>
    /// The run settled as failed inside this process and the live terminal-failure handler is recovering
    /// it. Nothing restarted, so no text may say so, and what is said about recovery depends on whether a
    /// replacement was actually queued.
    /// </summary>
    RunFailure
}
