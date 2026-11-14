using BotNexus.Domain.Security;

namespace BotNexus.Domain.Tests;

/// <summary>
/// The point of <see cref="SecretRef"/> is that a field meant to hold a pointer cannot quietly
/// hold the credential instead, so the rejection cases matter more than the acceptance ones.
/// </summary>
public sealed class SecretRefTests
{
    [Fact]
    public void Parse_SplitsSchemeFromIdentifier()
    {
        var reference = SecretRef.Parse("env:PROXMOX_TOKEN");

        reference.Scheme.ShouldBe("env");
        reference.Identifier.ShouldBe("PROXMOX_TOKEN");
    }

    // The whole reason the type exists: a pasted password has no scheme and must not parse.
    [Theory]
    [InlineData("hunter2")]
    [InlineData("correct horse battery staple")]
    [InlineData("s3cr3t-token-value")]
    public void TryParse_RejectsABareCredential(string literal)
    {
        SecretRef.TryParse(literal, out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNull();
        error.ShouldContain("scheme:identifier");
    }

    // The error is read by someone editing config.json, and may well be logged, so it must not
    // repeat back the thing it is complaining about.
    [Fact]
    public void TryParse_ErrorDoesNotEchoTheValue()
    {
        const string Literal = "definitely-a-real-password";

        SecretRef.TryParse(Literal, out _, out var error);

        error.ShouldNotBeNull();
        error.ShouldNotContain(Literal);
    }

    [Fact]
    public void TryParse_OverLongValueIsRejectedWithoutEchoingIt()
    {
        var overLong = "env:" + new string('x', SecretRef.MaxLength);

        SecretRef.TryParse(overLong, out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNull();
        error.ShouldNotContain(overLong);
        error.ShouldContain("secret value rather than a reference");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_RejectsEmpty(string? value)
        => SecretRef.TryParse(value, out _, out _).ShouldBeFalse();

    [Fact]
    public void TryParse_RejectsAnEmptyIdentifier()
    {
        SecretRef.TryParse("env:", out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNull();
        error.ShouldContain("names nothing to look up");
    }

    [Fact]
    public void TryParse_RejectsAnEmptyScheme()
        => SecretRef.TryParse(":PROXMOX_TOKEN", out _, out _).ShouldBeFalse();

    // A colon does not make something a reference. This is the near-miss case: a credential that
    // happens to contain a colon must not be accepted as though it named a store.
    [Theory]
    [InlineData("p@ss:word")]
    [InlineData("1234:5678")]
    [InlineData("two words:value")]
    public void TryParse_RejectsAMalformedScheme(string value)
        => SecretRef.TryParse(value, out _, out _).ShouldBeFalse();

    // Surrounding whitespace is tolerated rather than rejected: it is the kind of thing that
    // survives hand-editing JSON, and treating it as an error would fail a reference that is
    // otherwise exactly right.
    [Fact]
    public void TryParse_ToleratesSurroundingWhitespace()
    {
        var reference = SecretRef.Parse("  env:PROXMOX_TOKEN  ");

        reference.Scheme.ShouldBe("env");
        reference.Identifier.ShouldBe("PROXMOX_TOKEN");
    }

    [Fact]
    public void TryParse_LowerCasesTheSchemeButPreservesTheIdentifier()
    {
        var reference = SecretRef.Parse("ENV:Mixed_Case_Name");

        reference.Scheme.ShouldBe("env");
        reference.Identifier.ShouldBe("Mixed_Case_Name");
    }

    // A Windows path, or any identifier with its own colon, must keep everything after the first.
    [Fact]
    public void TryParse_SplitsOnTheFirstColonOnly()
    {
        var reference = SecretRef.Parse(@"file:C:\secrets\proxmox");

        reference.Scheme.ShouldBe("file");
        reference.Identifier.ShouldBe(@"C:\secrets\proxmox");
    }

    [Fact]
    public void ToString_RoundTrips()
        => SecretRef.Parse("file:~/.botnexus/secrets/proxmox")
            .ToString().ShouldBe("file:~/.botnexus/secrets/proxmox");

    // Deliberately the opposite of Secret.ToString: a reference names a location, and printing it
    // is what makes a resolution failure diagnosable.
    [Fact]
    public void ToString_IsNotRedacted()
        => SecretRef.Parse("env:PROXMOX_TOKEN").ToString().ShouldContain("PROXMOX_TOKEN");

    [Fact]
    public void Default_HasNoValue()
    {
        var reference = default(SecretRef);

        reference.HasValue.ShouldBeFalse();
        reference.ToString().ShouldBe("SecretRef(none)");
        Should.Throw<InvalidOperationException>(() => reference.Scheme);
        Should.Throw<InvalidOperationException>(() => reference.Identifier);
    }

    [Fact]
    public void Parse_ThrowsOnAMalformedValue()
        => Should.Throw<ArgumentException>(() => SecretRef.Parse("no-scheme-here"));

    [Theory]
    [InlineData("env:NAME")]
    [InlineData("file:/etc/x")]
    [InlineData("sqlite:key")]
    [InlineData("keyring:service/account")]
    [InlineData("vault-v2:path")]
    [InlineData("a+b.c-d:x")]
    public void TryParse_AcceptsWellFormedSchemes(string value)
        => SecretRef.TryParse(value, out _, out _).ShouldBeTrue();
}
