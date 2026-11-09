using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Specifies supported values for session lifecycle event type.
/// </summary>
public enum SessionLifecycleEventType { Created, MessageAdded, Closed, TerminalFailure, Expired, Deleted }

/// <summary>
/// Represents session lifecycle event.
/// </summary>
/// <param name="SessionId">The session the event is about.</param>
/// <param name="AgentId">The agent that owns the session.</param>
/// <param name="Type">What happened.</param>
/// <param name="Session">The live session aggregate, when the publisher has one.</param>
/// <param name="LeaseId">
/// For <see cref="SessionLifecycleEventType.TerminalFailure"/>, the run lease of the run that failed,
/// captured when the event is published. A subscriber acts on that lease and on no other, however much
/// later it handles the event and whatever has run on the session since. Null for every other event.
/// </param>
public sealed record SessionLifecycleEvent(
    string SessionId,
    string AgentId,
    SessionLifecycleEventType Type,
    GatewaySession? Session,
    RunLeaseId? LeaseId = null);
