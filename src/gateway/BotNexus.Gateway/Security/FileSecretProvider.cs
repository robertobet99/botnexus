using System.IO.Abstractions;
using BotNexus.Domain.Paths;
using BotNexus.Domain.Security;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Security;

/// <summary>
/// Resolves <c>file:/path/to/secret</c> from a file holding one credential.
/// </summary>
/// <remarks>
/// <para>
/// Easier to rotate per target than an environment variable, and manageable with ordinary
/// configuration tooling. Its protection is filesystem permissions, so the provider refuses a
/// file that others can read: a secret file readable by every account on the box provides no
/// protection at all, and failing loudly is better than appearing to work. That check goes
/// through <see cref="SecureFilePermissions"/> — the same helper the write paths use, and the one
/// that handles POSIX modes and Windows ACLs rather than only one of them.
/// </para>
/// <para>
/// The file's content is trimmed of trailing newlines. Every ordinary way of creating one — a
/// text editor, <c>echo</c>, a heredoc — appends a newline, and a credential that silently
/// carries <c>"\n"</c> fails authentication in a way that looks like a wrong password rather than
/// a formatting mistake. Leading whitespace is left alone: it is not something a tool adds by
/// accident, and it can legitimately be part of a credential.
/// </para>
/// </remarks>
public sealed class FileSecretProvider : ISecretProvider
{
    private readonly IFileSystem _fileSystem;

    /// <summary>Creates a provider over the real filesystem.</summary>
    public FileSecretProvider()
        : this(new FileSystem())
    {
    }

    /// <summary>Creates a provider over an injected filesystem, for tests.</summary>
    public FileSecretProvider(IFileSystem fileSystem)
        => _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    /// <inheritdoc />
    public string Scheme => "file";

    /// <inheritdoc />
    public async Task<Secret> ResolveAsync(SecretRef reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = HomePathExpander.Expand(reference.Identifier);
        if (string.IsNullOrWhiteSpace(path))
            throw new SecretResolutionException(reference, "the reference names no path.");

        // A relative path would resolve against the gateway's working directory, which is an
        // installation detail the operator writing config.json has no reason to know.
        if (!_fileSystem.Path.IsPathRooted(path))
            throw new SecretResolutionException(reference, $"'{reference.Identifier}' must be an absolute path, or start with '~'.");

        var fullPath = _fileSystem.Path.GetFullPath(path);

        if (!_fileSystem.File.Exists(fullPath))
            throw new SecretResolutionException(reference, $"no file at '{fullPath}'.");

        if (SecureFilePermissions.IsReadableByOthers(_fileSystem, fullPath))
        {
            throw new SecretResolutionException(
                reference,
                $"'{fullPath}' is readable by other users. Restrict it to its owner (chmod 600) and try again.");
        }

        string raw;
        try
        {
            raw = await _fileSystem.File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The inner exception is attached for diagnosis but the message stays about the path,
            // never the contents.
            throw new SecretResolutionException(reference, $"'{fullPath}' could not be read: {ex.Message}", ex);
        }

        var trimmed = raw.TrimEnd('\r', '\n');

        if (!Secret.TryCreate(trimmed, out var secret))
        {
            throw new SecretResolutionException(
                reference,
                trimmed.Length == 0
                    ? $"'{fullPath}' is empty."
                    : $"'{fullPath}' holds {trimmed.Length} characters, over the {Secret.MaxLength} character limit. Is this the right file?");
        }

        return secret;
    }
}
