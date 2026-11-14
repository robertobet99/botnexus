using BotNexus.Domain.Security;

namespace BotNexus.Domain.Tests;

/// <summary>
/// The value of <see cref="Secret"/> is entirely in what it refuses to print, so most of these
/// tests are about the paths a credential escapes through when nobody is being careful:
/// interpolation, a log placeholder, an exception message, a serialiser.
/// </summary>
public sealed class SecretTests
{
    private const string Plaintext = "hunter2-correct-horse-battery-staple";

    [Fact]
    public void Reveal_ReturnsThePlaintext()
        => Secret.Create(Plaintext).Reveal().ShouldBe(Plaintext);

    [Fact]
    public void ToString_IsRedacted()
        => Secret.Create(Plaintext).ToString().ShouldBe(Secret.RedactedMarker);

    // The failure this type exists to prevent. $"{secret}" is the single most likely way a
    // credential reaches a log line.
    [Fact]
    public void Interpolation_DoesNotContainThePlaintext()
    {
        var secret = Secret.Create(Plaintext);

        var line = $"connecting with {secret}";

        line.ShouldNotContain(Plaintext);
        line.ShouldBe($"connecting with {Secret.RedactedMarker}");
    }

    // A record struct's compiler-generated printing walks the backing fields, so without the
    // PrintMembers override this is where the plaintext would come out.
    [Fact]
    public void RecordMemberPrinting_DoesNotContainThePlaintext()
    {
        var wrapper = new { Credential = Secret.Create(Plaintext) };

        (wrapper.ToString() ?? string.Empty).ShouldNotContain(Plaintext);
    }

    [Fact]
    public void FormattedIntoAStructuredLogPlaceholder_DoesNotContainThePlaintext()
    {
        var secret = Secret.Create(Plaintext);

        string.Format(System.Globalization.CultureInfo.InvariantCulture, "token={0}", secret)
            .ShouldNotContain(Plaintext);
    }

    [Fact]
    public void CreateFailure_DoesNotEchoTheValue()
    {
        var overLong = new string('x', Secret.MaxLength + 1);

        var ex = Should.Throw<ArgumentException>(() => Secret.Create(overLong));

        ex.Message.ShouldNotContain(overLong);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryCreate_RejectsEmpty(string? value)
        => Secret.TryCreate(value, out _).ShouldBeFalse();

    [Fact]
    public void TryCreate_AcceptsExactlyMaxLength()
        => Secret.TryCreate(new string('x', Secret.MaxLength), out _).ShouldBeTrue();

    [Fact]
    public void TryCreate_RejectsOverMaxLength()
        => Secret.TryCreate(new string('x', Secret.MaxLength + 1), out _).ShouldBeFalse();

    // Unlike WebhookSecret, this holds credentials someone else issued, so it must not impose a
    // character set. A password with spaces, punctuation or non-ASCII is still a password.
    [Theory]
    [InlineData("p@ss w0rd!")]
    [InlineData("contains:a:colon")]
    [InlineData("naïve-pässwörd-😀")]
    [InlineData("{\"json\":\"like\"}")]
    public void TryCreate_AcceptsArbitraryCharacters(string value)
    {
        Secret.TryCreate(value, out var secret).ShouldBeTrue();
        secret.Reveal().ShouldBe(value);
    }

    [Fact]
    public void Default_HasNoValue()
    {
        var secret = default(Secret);

        secret.HasValue.ShouldBeFalse();
        secret.Length.ShouldBe(0);
        Should.Throw<InvalidOperationException>(() => secret.Reveal());
    }

    [Fact]
    public void Default_ToStringIsStillRedacted()
        => default(Secret).ToString().ShouldBe(Secret.RedactedMarker);

    [Fact]
    public void Length_ReportsWithoutRevealing()
        => Secret.Create(Plaintext).Length.ShouldBe(Plaintext.Length);

    [Fact]
    public void Equals_MatchesTheSamevalue()
        => Secret.Create(Plaintext).Equals(Secret.Create(Plaintext)).ShouldBeTrue();

    [Fact]
    public void Equals_RejectsADifferentValue()
        => Secret.Create(Plaintext).Equals(Secret.Create("something else")).ShouldBeFalse();

    // A default instance matching anything would make "no credential configured" equal to
    // "credential matched".
    [Fact]
    public void Equals_DefaultNeverMatches()
    {
        default(Secret).Equals(Secret.Create(Plaintext)).ShouldBeFalse();
        Secret.Create(Plaintext).Equals(default).ShouldBeFalse();
        default(Secret).Equals(default(Secret)).ShouldBeFalse();
    }

    [Fact]
    public void GetHashCode_IsConsistentWithEquals()
        => Secret.Create(Plaintext).GetHashCode().ShouldBe(Secret.Create(Plaintext).GetHashCode());

    [Fact]
    public void GetHashCode_DoesNotExposeThePlaintext()
        => Secret.Create(Plaintext).GetHashCode().ShouldNotBe(Plaintext.GetHashCode());
}
