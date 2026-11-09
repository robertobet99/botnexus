using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Where a <see cref="RunLease"/> is in its life. The state is what lets a later reader tell a run that
/// is (or was) in flight from one that settled and is waiting for a recovery decision, without guessing
/// from the transcript.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunLeaseState
{
    /// <summary>
    /// A run was admitted for the input and has not settled. Found at startup with no live turn, it means
    /// the process went away mid-run.
    /// </summary>
    Running,

    /// <summary>
    /// The run settled as failed in this process. The lease stays on the sentinel as the durable record
    /// that recovery owes this input a decision, and carries what is known about the failure.
    /// </summary>
    Failed,

    /// <summary>
    /// Recovery decided to re-admit the input and recorded that before posting the replacement. The
    /// replacement's turn takes the lease over and makes it <see cref="Running"/>.
    /// </summary>
    Pending
}
