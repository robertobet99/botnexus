using BotNexus.Domain.Security;

namespace BotNexus.Gateway.Security;

/// <summary>
/// Resolves <c>env:NAME</c> from the process environment.
/// </summary>
/// <remarks>
/// The simplest store, and the one that matches how provider API keys already reach the gateway
/// via <c>EnvironmentApiKeys</c>. Its protection is process isolation: anything able to read
/// <c>/proc/&lt;pid&gt;/environ</c> as the same user can read these, so it keeps credentials out
/// of the repository and out of an agent's context, not away from a local attacker.
/// </remarks>
public sealed class EnvironmentSecretProvider : ISecretProvider
{
    private readonly Func<string, string?> _readVariable;

    /// <summary>Creates a provider reading the real process environment.</summary>
    public EnvironmentSecretProvider()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>Creates a provider over an injected reader, for tests.</summary>
    public EnvironmentSecretProvider(Func<string, string?> readVariable)
        => _readVariable = readVariable ?? throw new ArgumentNullException(nameof(readVariable));

    /// <inheritdoc />
    public string Scheme => "env";

    /// <inheritdoc />
    public Task<Secret> ResolveAsync(SecretRef reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var raw = _readVariable(reference.Identifier);

        // An unset variable and one set to empty are the same mistake from the operator's side,
        // and both must fail rather than hand back a Secret that is technically valid but useless.
        if (string.IsNullOrEmpty(raw))
        {
            throw new SecretResolutionException(
                reference,
                $"environment variable '{reference.Identifier}' is not set, or is empty.");
        }

        if (!Secret.TryCreate(raw, out var secret))
        {
            throw new SecretResolutionException(
                reference,
                $"environment variable '{reference.Identifier}' holds a value longer than the {Secret.MaxLength} character limit.");
        }

        return Task.FromResult(secret);
    }
}
