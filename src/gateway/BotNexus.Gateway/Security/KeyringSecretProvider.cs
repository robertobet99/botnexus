using System.Diagnostics;
using System.Runtime.Versioning;
using BotNexus.Domain.Security;

namespace BotNexus.Gateway.Security;

/// <summary>
/// Resolves <c>keyring:service/account</c> from the operating system's credential store.
/// </summary>
/// <remarks>
/// <para>
/// The only backend here that protects a credential at rest: the OS holds it encrypted and
/// releases it to the logged-in user. That makes it the right choice on a workstation, and an
/// awkward one on a server - a Secret Service daemon needs a session bus and an unlocked keyring,
/// neither of which a headless host has by default. When it is not available this fails with an
/// instruction rather than a stack trace, because "keyring unavailable" is a setup problem the
/// operator can act on.
/// </para>
/// <para>
/// <b>Platform support is uneven and stated plainly rather than implied.</b> Linux goes through
/// <c>secret-tool</c> (libsecret) and macOS through <c>security</c>, both of which are the
/// documented interfaces to those stores. Windows has no equivalent built-in command - retrieving
/// a Credential Manager secret needs <c>CredReadW</c> - so it reports that the scheme is
/// unsupported there and points at <c>env:</c> or <c>file:</c>. Shipping an untested P/Invoke
/// would be worse than saying so.
/// </para>
/// </remarks>
public sealed class KeyringSecretProvider : ISecretProvider
{
    /// <summary>Result of running a keyring lookup command.</summary>
    /// <param name="Found">Whether the tool was present and exited successfully.</param>
    /// <param name="ExitCode">Process exit code, or -1 when the tool could not be started.</param>
    /// <param name="StandardOutput">Raw stdout.</param>
    /// <param name="Failure">Why the command could not run at all, or null.</param>
    public sealed record LookupResult(bool Found, int ExitCode, string StandardOutput, string? Failure);

    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<LookupResult>> _runLookup;
    private readonly bool _isLinux;
    private readonly bool _isMacOs;

    /// <summary>Creates a provider that shells out to the platform's keyring tool.</summary>
    public KeyringSecretProvider()
        : this(RunProcessAsync, OperatingSystem.IsLinux(), OperatingSystem.IsMacOS())
    {
    }

    /// <summary>Creates a provider over an injected runner and platform, for tests.</summary>
    public KeyringSecretProvider(
        Func<string, IReadOnlyList<string>, CancellationToken, Task<LookupResult>> runLookup,
        bool isLinux,
        bool isMacOs)
    {
        _runLookup = runLookup ?? throw new ArgumentNullException(nameof(runLookup));
        _isLinux = isLinux;
        _isMacOs = isMacOs;
    }

    /// <inheritdoc />
    public string Scheme => "keyring";

    /// <inheritdoc />
    public async Task<Secret> ResolveAsync(SecretRef reference, CancellationToken cancellationToken = default)
    {
        var (service, account) = SplitIdentifier(reference);

        var (executable, arguments, installHint) = ResolveCommand(reference, service, account);

        var result = await _runLookup(executable, arguments, cancellationToken).ConfigureAwait(false);

        if (result.Failure is not null)
            throw new SecretResolutionException(reference, $"{result.Failure} {installHint}");

        // secret-tool and security both exit non-zero when the item is absent, and print nothing
        // useful, so the message has to come from us.
        if (!result.Found || result.ExitCode != 0)
        {
            throw new SecretResolutionException(
                reference,
                $"no keyring entry for service '{service}' account '{account}'. "
                + $"Add one with: {DescribeStoreCommand(service, account)}");
        }

        // Both tools terminate the value with a newline. Trimming the end only - a credential can
        // legitimately begin with whitespace, and neither tool adds any.
        var value = result.StandardOutput.TrimEnd('\r', '\n');

        if (!Secret.TryCreate(value, out var secret))
        {
            throw new SecretResolutionException(
                reference,
                value.Length == 0
                    ? $"the keyring entry for '{service}/{account}' is empty."
                    : $"the keyring entry for '{service}/{account}' exceeds the {Secret.MaxLength} character limit.");
        }

        return secret;
    }

    /// <summary>
    /// Splits <c>service/account</c>. A bare identifier is treated as the account under a default
    /// service, because <c>keyring:my-token</c> is what people write first.
    /// </summary>
    private static (string Service, string Account) SplitIdentifier(SecretRef reference)
    {
        var identifier = reference.Identifier;
        var slash = identifier.IndexOf('/');
        return slash <= 0
            ? ("botnexus", identifier)
            : (identifier[..slash], identifier[(slash + 1)..]);
    }

    private (string Executable, IReadOnlyList<string> Arguments, string InstallHint) ResolveCommand(
        SecretRef reference,
        string service,
        string account)
    {
        if (_isLinux)
        {
            return ("secret-tool",
                    ["lookup", "service", service, "account", account],
                    "Install libsecret-tools and run a Secret Service daemon, or use env: or file: instead.");
        }

        if (_isMacOs)
        {
            return ("security",
                    ["find-generic-password", "-s", service, "-a", account, "-w"],
                    "The 'security' command ships with macOS; if it is missing the keychain is unavailable.");
        }

        throw new SecretResolutionException(
            reference,
            "the keyring scheme is not supported on this platform. Use env: or file: instead.");
    }

    private string DescribeStoreCommand(string service, string account)
        => _isLinux
            ? $"secret-tool store --label='BotNexus {account}' service {service} account {account}"
            : $"security add-generic-password -s {service} -a {account} -w";

    [UnsupportedOSPlatform("browser")]
    private static async Task<LookupResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return new LookupResult(false, -1, string.Empty, $"could not start '{executable}'.");

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new LookupResult(true, process.ExitCode, stdout, null);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The tool is not installed. The commonest case on a server, and the one worth a
            // remedy rather than an exception type.
            return new LookupResult(false, -1, string.Empty, $"'{executable}' is not installed.");
        }
    }
}
