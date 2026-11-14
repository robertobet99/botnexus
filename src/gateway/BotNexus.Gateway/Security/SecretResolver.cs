using BotNexus.Domain.Security;

namespace BotNexus.Gateway.Security;

/// <summary>
/// Dispatches a <see cref="SecretRef"/> to the provider registered for its scheme.
/// </summary>
/// <remarks>
/// Providers are supplied by dependency injection rather than being listed here, so adding a
/// store is a registration rather than an edit to this class. Two providers claiming the same
/// scheme is a configuration error and fails loudly at construction — the alternative is one of
/// them silently winning, which is the sort of thing nobody discovers until a credential resolves
/// from somewhere unexpected.
/// </remarks>
public sealed class SecretResolver : ISecretResolver
{
    private readonly Dictionary<string, ISecretProvider> _providers;

    /// <summary>Creates a resolver over the registered providers.</summary>
    /// <exception cref="ArgumentException">Two providers claim the same scheme.</exception>
    public SecretResolver(IEnumerable<ISecretProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _providers = new Dictionary<string, ISecretProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (!_providers.TryAdd(provider.Scheme, provider))
            {
                throw new ArgumentException(
                    $"More than one secret provider claims the scheme '{provider.Scheme}'. "
                    + "Each scheme must have exactly one provider, or which store a credential comes from is undefined.",
                    nameof(providers));
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedSchemes => _providers.Keys;

    /// <inheritdoc />
    public Task<Secret> ResolveAsync(SecretRef reference, CancellationToken cancellationToken = default)
    {
        if (!reference.HasValue)
            throw new ArgumentException("Reference was never parsed through SecretRef.TryParse.", nameof(reference));

        if (!_providers.TryGetValue(reference.Scheme, out var provider))
        {
            var known = _providers.Count == 0
                ? "none are registered"
                : string.Join(", ", _providers.Keys.Order(StringComparer.Ordinal));
            throw new SecretResolutionException(reference, $"no provider is registered for scheme '{reference.Scheme}' (known schemes: {known}).");
        }

        return provider.ResolveAsync(reference, cancellationToken);
    }
}
