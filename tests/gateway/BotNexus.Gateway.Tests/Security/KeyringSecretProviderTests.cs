using BotNexus.Domain.Security;
using BotNexus.Gateway.Security;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// The keyring backend shells out to the platform's own tool, so the tool itself is not what is
/// under test here - the command construction, the identifier split, and above all the failure
/// messages are. Those messages are the whole value of this provider on a host where no keyring is
/// installed, which is the common case on a server.
/// </summary>
public sealed class KeyringSecretProviderTests
{
    private sealed record Invocation(string Executable, IReadOnlyList<string> Arguments);

    private static (KeyringSecretProvider Provider, List<Invocation> Calls) Linux(
        KeyringSecretProvider.LookupResult result)
    {
        var calls = new List<Invocation>();
        var provider = new KeyringSecretProvider(
            (exe, args, _) => { calls.Add(new Invocation(exe, args)); return Task.FromResult(result); },
            isLinux: true, isMacOs: false);
        return (provider, calls);
    }

    private static KeyringSecretProvider MacOs(KeyringSecretProvider.LookupResult result)
        => new((_, _, _) => Task.FromResult(result), isLinux: false, isMacOs: true);

    private static KeyringSecretProvider.LookupResult Found(string output)
        => new(Found: true, ExitCode: 0, StandardOutput: output, Failure: null);

    private static KeyringSecretProvider.LookupResult NotFound()
        => new(Found: true, ExitCode: 1, StandardOutput: string.Empty, Failure: null);

    [Fact]
    public async Task ResolveAsync_ReturnsTheKeyringValue()
    {
        var (provider, _) = Linux(Found("s3cret\n"));

        (await provider.ResolveAsync(SecretRef.Parse("keyring:botnexus/proxmox"))).Reveal().ShouldBe("s3cret");
    }

    [Fact]
    public async Task ResolveAsync_Linux_UsesSecretToolWithServiceAndAccount()
    {
        var (provider, calls) = Linux(Found("v"));

        await provider.ResolveAsync(SecretRef.Parse("keyring:myservice/myaccount"));

        var call = calls.ShouldHaveSingleItem();
        call.Executable.ShouldBe("secret-tool");
        call.Arguments.ShouldBe(["lookup", "service", "myservice", "account", "myaccount"]);
    }

    // keyring:my-token is what people write first, so it has to mean something sensible.
    [Fact]
    public async Task ResolveAsync_BareIdentifier_UsesTheDefaultService()
    {
        var (provider, calls) = Linux(Found("v"));

        await provider.ResolveAsync(SecretRef.Parse("keyring:my-token"));

        calls[0].Arguments.ShouldBe(["lookup", "service", "botnexus", "account", "my-token"]);
    }

    [Fact]
    public async Task ResolveAsync_MacOs_UsesSecurityWithW()
    {
        var provider = MacOs(Found("s3cret\n"));

        (await provider.ResolveAsync(SecretRef.Parse("keyring:svc/acct"))).Reveal().ShouldBe("s3cret");
    }

    // The tool prints nothing useful when the entry is missing, so the remedy has to come from us.
    [Fact]
    public async Task ResolveAsync_EntryMissing_NamesTheServiceAndAccountAndHowToAddIt()
    {
        var (provider, _) = Linux(NotFound());

        var ex = await Should.ThrowAsync<SecretResolutionException>(
            () => provider.ResolveAsync(SecretRef.Parse("keyring:svc/acct")));

        ex.Message.ShouldContain("svc");
        ex.Message.ShouldContain("acct");
        ex.Message.ShouldContain("secret-tool store");
    }

    // The common case on a server, and the reason this provider must fail with an instruction.
    [Fact]
    public async Task ResolveAsync_ToolNotInstalled_SaysSoAndOffersAnAlternative()
    {
        var provider = new KeyringSecretProvider(
            (_, _, _) => Task.FromResult(new KeyringSecretProvider.LookupResult(
                false, -1, string.Empty, "'secret-tool' is not installed.")),
            isLinux: true, isMacOs: false);

        var ex = await Should.ThrowAsync<SecretResolutionException>(
            () => provider.ResolveAsync(SecretRef.Parse("keyring:svc/acct")));

        ex.Message.ShouldContain("not installed");
        ex.Message.ShouldContain("libsecret-tools");
        ex.Message.ShouldContain("env:");
    }

    // Stating the gap beats shipping an untested P/Invoke and pretending.
    [Fact]
    public async Task ResolveAsync_UnsupportedPlatform_SaysWhatToUseInstead()
    {
        var provider = new KeyringSecretProvider(
            (_, _, _) => throw new InvalidOperationException("should not be reached"),
            isLinux: false, isMacOs: false);

        var ex = await Should.ThrowAsync<SecretResolutionException>(
            () => provider.ResolveAsync(SecretRef.Parse("keyring:svc/acct")));

        ex.Message.ShouldContain("not supported on this platform");
        ex.Message.ShouldContain("file:");
    }

    // Both tools terminate with a newline; a credential carrying one fails at the far end looking
    // like a wrong password.
    [Theory]
    [InlineData("s3cret\n")]
    [InlineData("s3cret\r\n")]
    public async Task ResolveAsync_TrimsTheTrailingNewline(string output)
    {
        var (provider, _) = Linux(Found(output));

        (await provider.ResolveAsync(SecretRef.Parse("keyring:svc/acct"))).Reveal().ShouldBe("s3cret");
    }

    [Fact]
    public async Task ResolveAsync_PreservesLeadingWhitespace()
    {
        var (provider, _) = Linux(Found("  s3cret\n"));

        (await provider.ResolveAsync(SecretRef.Parse("keyring:svc/acct"))).Reveal().ShouldBe("  s3cret");
    }

    [Fact]
    public async Task ResolveAsync_EmptyEntry_Throws()
    {
        var (provider, _) = Linux(Found("\n"));

        var ex = await Should.ThrowAsync<SecretResolutionException>(
            () => provider.ResolveAsync(SecretRef.Parse("keyring:svc/acct")));

        ex.Message.ShouldContain("empty");
    }

    [Fact]
    public async Task ResolveAsync_FailureMessage_NeverContainsAValue()
    {
        const string Value = "a-real-looking-credential";
        var (provider, _) = Linux(new KeyringSecretProvider.LookupResult(true, 1, Value, null));

        var ex = await Should.ThrowAsync<SecretResolutionException>(
            () => provider.ResolveAsync(SecretRef.Parse("keyring:svc/acct")));

        ex.Message.ShouldNotContain(Value);
    }

    [Fact]
    public void Scheme_IsKeyring() => new KeyringSecretProvider().Scheme.ShouldBe("keyring");
}
