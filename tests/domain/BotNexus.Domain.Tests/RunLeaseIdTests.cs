using System.Text.Json;
using BotNexus.Domain.Primitives;
using Vogen;

namespace BotNexus.Domain.Tests;

public sealed class RunLeaseIdTests
{
    [Fact]
    public void Create_GeneratesUnique32CharacterIdentities()
    {
        var a = RunLeaseId.Create();
        var b = RunLeaseId.Create();

        a.Value.Length.ShouldBe(32);
        a.ShouldNotBe(b);
    }

    [Fact]
    public void From_TrimsLeadingAndTrailingWhitespace()
    {
        RunLeaseId.From(" lease-1 ").Value.ShouldBe("lease-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("   \n   ")]
    public void From_RejectsEmptyOrWhitespace(string value)
    {
        Action act = () => RunLeaseId.From(value);

        var ex = act.ShouldThrow<ValueObjectValidationException>();
        ex.Message.ShouldContain("RunLeaseId");
    }

    [Fact]
    public void Equality_MatchesByValue()
    {
        RunLeaseId.From("lease-1").ShouldBe(RunLeaseId.From("lease-1"));
        RunLeaseId.From("lease-1").ShouldNotBe(RunLeaseId.From("lease-2"));
    }

    [Fact]
    public void ToString_ReturnsRawValue()
    {
        RunLeaseId.From("lease-1").ToString().ShouldBe("lease-1");
    }

    [Fact]
    public void Json_SerializesAsBareStringAndRoundTrips()
    {
        var original = RunLeaseId.From("lease-1");

        var json = JsonSerializer.Serialize(original);

        json.ShouldBe("\"lease-1\"");
        JsonSerializer.Deserialize<RunLeaseId>(json).ShouldBe(original);
    }
}
