using BotNexus.Domain.Security;

namespace BotNexus.Gateway.Security;

/// <summary>
/// Resolves a <see cref="SecretRef"/> to the credential it points at.
/// </summary>
/// <remarks>
/// Resolution happens at the moment a credential is used, not once at start-up. That is what lets
/// an operator rotate a credential without restarting the gateway, and it keeps the plaintext out
/// of long-lived objects that a heap dump or a careless log statement could reach.
/// </remarks>
public interface ISecretResolver
{
    /// <summary>Resolves a reference, or throws <see cref="SecretResolutionException"/>.</summary>
    Task<Secret> ResolveAsync(SecretRef reference, CancellationToken cancellationToken = default);

    /// <summary>The schemes this resolver can serve, for diagnostics and validation.</summary>
    IReadOnlyCollection<string> SupportedSchemes { get; }
}

/// <summary>
/// Resolves references for a single scheme. One implementation per secret store.
/// </summary>
public interface ISecretProvider
{
    /// <summary>The scheme this provider claims, lower-case: <c>env</c>, <c>file</c>, …</summary>
    string Scheme { get; }

    /// <summary>
    /// Resolves a reference already known to carry this provider's scheme.
    /// </summary>
    /// <exception cref="SecretResolutionException">The credential is missing or unusable.</exception>
    Task<Secret> ResolveAsync(SecretRef reference, CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown when a reference cannot be resolved.
/// </summary>
/// <remarks>
/// The message names the <i>reference</i> and never the value: a resolution failure is one of the
/// most common ways a credential ends up in a log, because the natural thing to write is "could
/// not use secret X". <see cref="SecretRef"/> is safe to print, which is why it is the thing
/// carried here.
/// </remarks>
public sealed class SecretResolutionException : Exception
{
    /// <summary>Creates the exception for a reference that could not be resolved.</summary>
    public SecretResolutionException(SecretRef reference, string reason, Exception? innerException = null)
        : base($"Could not resolve credential '{reference}': {reason}", innerException)
        => Reference = reference;

    /// <summary>The reference that failed. Safe to log.</summary>
    public SecretRef Reference { get; }
}
