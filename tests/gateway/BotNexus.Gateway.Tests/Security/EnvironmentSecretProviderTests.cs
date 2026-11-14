using BotNexus.Domain.Security;
using BotNexus.Gateway.Security;

namespace BotNexus.Gateway.Tests.Security;

public sealed class EnvironmentSecretProviderTests
{
    private static EnvironmentSecretProvider Provider(params (string Name, string? Value)[] variables)
    {
        var map = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return new EnvironmentSecretProvider(name => map.GetValueOrDefault(name));
    }

    [Fact]
    public async Task ResolveAsync_ReturnsTheVariableValue()
    {
        var secret = await Provider(("PROXMOX_TOKEN", "s3cret")).ResolveAsync(SecretRef.Parse("env:PROXMOX_TOKEN"));

        secret.Reveal().ShouldBe("s3cret");
    }

    // Unset and empty are the same operator mistake and both must fail, rather than handing back
    // a technically-valid credential that fails later at the remote end.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ResolveAsync_MissingOrEmpty_Throws(string? value)
    {
        var ex = await Should.ThrowAsync<SecretResolutionException>(
            () => Provider(("PROXMOX_TOKEN", value)).ResolveAsync(SecretRef.Parse("env:PROXMOX_TOKEN")));

        ex.Message.ShouldContain("PROXMOX_TOKEN");
        ex.Message.ShouldContain("not set");
    }

    [Fact]
    public async Task ResolveAsync_OverLongValue_Throws()
    {
        var ex = await Should.ThrowAsync<SecretResolutionException>(
            () => Provider(("BIG", new string('x', Secret.MaxLength + 1))).ResolveAsync(SecretRef.Parse("env:BIG")));

        ex.Message.ShouldContain("limit");
    }

    // The message is the most likely place for a credential to escape, so it is asserted directly.
    [Fact]
    public async Task ResolveAsync_FailureMessage_NeverContainsAValue()
    {
        const string Value = "a-real-looking-credential";

        var ex = await Should.ThrowAsync<SecretResolutionException>(
            () => Provider(("PRESENT", Value)).ResolveAsync(SecretRef.Parse("env:ABSENT")));

        ex.Message.ShouldNotContain(Value);
    }

    [Fact]
    public void Scheme_IsEnv() => new EnvironmentSecretProvider().Scheme.ShouldBe("env");
}
