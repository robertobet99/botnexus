using System.IO.Abstractions;
using BotNexus.Domain.Security;
using BotNexus.Gateway.Security;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// These run against real files rather than a mock filesystem, because the behaviour under test
/// includes a permission check, and permissions are exactly the thing a mock would have to
/// pretend about.
/// </summary>
public sealed class FileSecretProviderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("botnexus-secret-tests").FullName;
    private readonly FileSecretProvider _provider = new(new FileSystem());

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private string WriteSecretFile(string contents, UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite)
    {
        var path = Path.Combine(_directory, Path.GetRandomFileName());
        File.WriteAllText(path, contents);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, mode);
        return path;
    }

    [Fact]
    public async Task ResolveAsync_ReturnsTheFileContents()
    {
        var path = WriteSecretFile("s3cret");

        (await _provider.ResolveAsync(SecretRef.Parse($"file:{path}"))).Reveal().ShouldBe("s3cret");
    }

    // Every ordinary way of creating one of these files appends a newline. A credential carrying
    // a trailing "\n" fails at the remote end looking like a wrong password rather than a
    // formatting mistake, so this is the single most valuable behaviour here.
    [Theory]
    [InlineData("s3cret\n")]
    [InlineData("s3cret\r\n")]
    [InlineData("s3cret\n\n")]
    public async Task ResolveAsync_TrimsTrailingNewlines(string contents)
    {
        var path = WriteSecretFile(contents);

        (await _provider.ResolveAsync(SecretRef.Parse($"file:{path}"))).Reveal().ShouldBe("s3cret");
    }

    // Leading whitespace is not something a tool adds by accident, and can be part of a credential.
    [Fact]
    public async Task ResolveAsync_PreservesLeadingWhitespace()
    {
        var path = WriteSecretFile("  s3cret\n");

        (await _provider.ResolveAsync(SecretRef.Parse($"file:{path}"))).Reveal().ShouldBe("  s3cret");
    }

    [Fact]
    public async Task ResolveAsync_MissingFile_Throws()
    {
        var path = Path.Combine(_directory, "does-not-exist");

        var ex = await Should.ThrowAsync<SecretResolutionException>(
            () => _provider.ResolveAsync(SecretRef.Parse($"file:{path}")));

        ex.Message.ShouldContain("no file at");
    }

    [Fact]
    public async Task ResolveAsync_EmptyFile_Throws()
    {
        var path = WriteSecretFile(string.Empty);

        var ex = await Should.ThrowAsync<SecretResolutionException>(
            () => _provider.ResolveAsync(SecretRef.Parse($"file:{path}")));

        ex.Message.ShouldContain("is empty");
    }

    // A relative path would resolve against the gateway's working directory - an installation
    // detail the person editing config.json has no reason to know.
    [Fact]
    public async Task ResolveAsync_RelativePath_Throws()
    {
        var ex = await Should.ThrowAsync<SecretResolutionException>(
            () => _provider.ResolveAsync(SecretRef.Parse("file:secrets/proxmox")));

        ex.Message.ShouldContain("absolute path");
    }

    // A secret file every account on the box can read provides no protection, so appearing to
    // work would be worse than failing.
    [Fact]
    public async Task ResolveAsync_WorldReadableFile_Throws()
    {
        if (OperatingSystem.IsWindows())
            return; // POSIX modes only; the Windows ACL equivalent is covered by SecureFilePermissions' own tests.

        var path = WriteSecretFile(
            "s3cret",
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        var ex = await Should.ThrowAsync<SecretResolutionException>(
            () => _provider.ResolveAsync(SecretRef.Parse($"file:{path}")));

        ex.Message.ShouldContain("readable by other users");
        ex.Message.ShouldContain("chmod 600");
    }

    [Fact]
    public async Task ResolveAsync_GroupReadableFile_Throws()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = WriteSecretFile(
            "s3cret",
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        await Should.ThrowAsync<SecretResolutionException>(
            () => _provider.ResolveAsync(SecretRef.Parse($"file:{path}")));
    }

    [Fact]
    public async Task ResolveAsync_FailureMessage_NeverContainsTheContents()
    {
        const string Contents = "a-real-looking-credential";
        if (OperatingSystem.IsWindows())
            return;

        var path = WriteSecretFile(
            Contents,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        var ex = await Should.ThrowAsync<SecretResolutionException>(
            () => _provider.ResolveAsync(SecretRef.Parse($"file:{path}")));

        ex.Message.ShouldNotContain(Contents);
    }

    [Fact]
    public void Scheme_IsFile() => _provider.Scheme.ShouldBe("file");
}
