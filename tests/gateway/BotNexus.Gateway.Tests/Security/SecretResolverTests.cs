using BotNexus.Domain.Security;
using BotNexus.Gateway.Security;

namespace BotNexus.Gateway.Tests.Security;

public sealed class SecretResolverTests
{
    private sealed class StubProvider(string scheme, string value) : ISecretProvider
    {
        public string Scheme => scheme;

        public Task<Secret> ResolveAsync(SecretRef reference, CancellationToken cancellationToken = default)
            => Task.FromResult(Secret.Create(value));
    }

    // The Phase 1 acceptance criterion: a credential round-trips from a reference to plaintext.
    [Fact]
    public async Task ResolveAsync_DispatchesToTheProviderForTheScheme()
    {
        var resolver = new SecretResolver([new StubProvider("env", "from-env"), new StubProvider("file", "from-file")]);

        (await resolver.ResolveAsync(SecretRef.Parse("env:ANY"))).Reveal().ShouldBe("from-env");
        (await resolver.ResolveAsync(SecretRef.Parse("file:/any"))).Reveal().ShouldBe("from-file");
    }

    [Fact]
    public async Task ResolveAsync_UnknownScheme_ThrowsNamingTheKnownOnes()
    {
        var resolver = new SecretResolver([new StubProvider("env", "x")]);

        var ex = await Should.ThrowAsync<SecretResolutionException>(
            () => resolver.ResolveAsync(SecretRef.Parse("keyring:whatever")));

        ex.Message.ShouldContain("keyring");
        ex.Message.ShouldContain("env");
        ex.Reference.Scheme.ShouldBe("keyring");
    }

    [Fact]
    public async Task ResolveAsync_NoProvidersAtAll_SaysSo()
    {
        var resolver = new SecretResolver([]);

        var ex = await Should.ThrowAsync<SecretResolutionException>(
            () => resolver.ResolveAsync(SecretRef.Parse("env:ANY")));

        ex.Message.ShouldContain("none are registered");
    }

    // Silently letting one win is how a credential ends up resolving from a store nobody expected.
    [Fact]
    public void Constructor_DuplicateScheme_Throws()
    {
        var ex = Should.Throw<ArgumentException>(
            () => new SecretResolver([new StubProvider("env", "a"), new StubProvider("env", "b")]));

        ex.Message.ShouldContain("more than one secret provider", Case.Insensitive);
    }

    [Fact]
    public async Task ResolveAsync_SchemeMatchingIsCaseInsensitive()
    {
        var resolver = new SecretResolver([new StubProvider("env", "value")]);

        // SecretRef lower-cases on parse, but a provider registering "ENV" must still match.
        var resolverWithUpper = new SecretResolver([new StubProvider("ENV", "value")]);

        (await resolver.ResolveAsync(SecretRef.Parse("ENV:X"))).Reveal().ShouldBe("value");
        (await resolverWithUpper.ResolveAsync(SecretRef.Parse("env:X"))).Reveal().ShouldBe("value");
    }

    [Fact]
    public void ResolveAsync_UnparsedReference_IsARejectedArgument()
        => Should.Throw<ArgumentException>(() => new SecretResolver([]).ResolveAsync(default));

    [Fact]
    public void SupportedSchemes_ListsTheRegisteredProviders()
        => new SecretResolver([new StubProvider("env", "a"), new StubProvider("file", "b")])
            .SupportedSchemes.OrderBy(s => s, StringComparer.Ordinal)
            .ShouldBe(["env", "file"]);
}
