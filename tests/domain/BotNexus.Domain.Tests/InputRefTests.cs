using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Domain.Tests;

public sealed class InputRefTests
{
    private static readonly DateTimeOffset Recorded = new DateTimeOffset(2026, 11, 8, 23, 1, 31, TimeSpan.Zero).AddTicks(1234567);

    [Fact]
    public void For_CapturesRoleInstantAndContentDigest()
    {
        var reference = InputRef.For(Entry("Apply the scheduled change.", MessageRole.User, Recorded));

        reference.Role.ShouldBe(MessageRole.User);
        reference.Timestamp.ShouldBe(Recorded);
        reference.ContentSha256.ShouldBe(Sha256Hex("Apply the scheduled change."));
    }

    [Fact]
    public void ContentSha256_IsTheLowercaseHexSha256OfTheUtf8Content()
    {
        // The NIST SHA-256 test vector for "abc": pins both the algorithm and the lowercase hex form.
        InputRef.For(Entry("abc", MessageRole.User, Recorded)).ContentSha256
            .ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Fact]
    public void Matches_TheEntryItWasBuiltFrom()
    {
        var entry = Entry("Apply the scheduled change.", MessageRole.User, Recorded);

        InputRef.For(entry).Matches(entry).ShouldBeTrue();
    }

    [Fact]
    public void Matches_TheSameInstantReadBackWithAnotherOffset()
    {
        var reference = InputRef.For(Entry("Apply the scheduled change.", MessageRole.User, Recorded));
        var readBack = Entry("Apply the scheduled change.", MessageRole.User, Recorded.ToOffset(TimeSpan.FromHours(5)));

        reference.Matches(readBack).ShouldBeTrue();
    }

    [Fact]
    public void Matches_RejectsAnyDifferenceInRoleInstantOrContent()
    {
        var reference = InputRef.For(Entry("Apply the scheduled change.", MessageRole.User, Recorded));

        reference.Matches(Entry("Apply the scheduled change.", MessageRole.Assistant, Recorded)).ShouldBeFalse("role");
        reference.Matches(Entry("Apply the scheduled change.", MessageRole.User, Recorded.AddTicks(1))).ShouldBeFalse("one tick apart");
        reference.Matches(Entry("Apply the scheduled change!", MessageRole.User, Recorded)).ShouldBeFalse("content");
    }

    [Fact]
    public void Resolve_FindsTheSingleMatch()
    {
        var target = Entry("Apply the scheduled change.", MessageRole.Assistant, Recorded);
        var history = new[]
        {
            Entry("Earlier message.", MessageRole.User, Recorded.AddMinutes(-5)),
            target,
            Entry("Later message.", MessageRole.User, Recorded.AddMinutes(5))
        };

        var resolution = InputRef.For(target).Resolve(history);

        resolution.Match.ShouldBe(InputRefMatch.Single);
        resolution.Entry.ShouldBeSameAs(target);
    }

    [Fact]
    public void Resolve_ReportsNoneWhenNothingMatches()
    {
        var reference = InputRef.For(Entry("Apply the scheduled change.", MessageRole.User, Recorded));

        var resolution = reference.Resolve([Entry("Something else.", MessageRole.User, Recorded)]);

        resolution.Match.ShouldBe(InputRefMatch.None);
        resolution.Entry.ShouldBeNull();
    }

    [Fact]
    public void Resolve_ReportsAmbiguousInsteadOfGuessingWhenTwoEntriesAreIndistinguishable()
    {
        var first = Entry("Apply the scheduled change.", MessageRole.User, Recorded);
        var second = Entry("Apply the scheduled change.", MessageRole.User, Recorded);

        var resolution = InputRef.For(first).Resolve([first, second]);

        resolution.Match.ShouldBe(InputRefMatch.Ambiguous);
        resolution.Entry.ShouldBeNull();
    }

    [Fact]
    public void Resolve_SeparatesEntriesWithTheSameContentRecordedAtDifferentInstants()
    {
        var original = Entry("Apply the scheduled change.", MessageRole.User, Recorded);
        var replayed = Entry("Apply the scheduled change.", MessageRole.User, Recorded.AddSeconds(30));
        var history = new[] { original, replayed };

        InputRef.For(original).Resolve(history).Entry.ShouldBeSameAs(original);
        InputRef.For(replayed).Resolve(history).Entry.ShouldBeSameAs(replayed);
    }

    [Fact]
    public void Resolve_StillFindsAnEntryCompactionFoldedAway()
    {
        var folded = Entry("Apply the scheduled change.", MessageRole.User, Recorded) with { IsHistory = true };

        InputRef.For(folded).Resolve([folded]).Match.ShouldBe(InputRefMatch.Single);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Json_RoundTripsToAnEqualReference(bool camelCase)
    {
        var options = camelCase ? new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase } : new JsonSerializerOptions();
        var original = InputRef.For(Entry("Apply the scheduled change.", MessageRole.User, Recorded.ToOffset(TimeSpan.FromHours(-8))));

        var json = JsonSerializer.Serialize(original, options);
        var readBack = JsonSerializer.Deserialize<InputRef>(json, options).ShouldNotBeNull();

        readBack.ShouldBe(original);
        readBack.Matches(Entry("Apply the scheduled change.", MessageRole.User, Recorded))
            .ShouldBeTrue("the instant survives whatever offset was serialized");
    }

    private static SessionEntry Entry(string content, MessageRole role, DateTimeOffset timestamp)
        => new() { Role = role, Content = content, Timestamp = timestamp };

    private static string Sha256Hex(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
