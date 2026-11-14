using System.Reflection;
using BotNexus.Domain.Security;
using BotNexus.Gateway.Tools;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fitness function for the exposure boundary of <c>list_locations</c>.
///
/// <para><b>What it pins.</b> <see cref="LocationEntry"/> is the only shape an agent sees of a
/// configured location, and it is a hand-written projection rather than a serialisation of
/// <c>LocationConfig</c>. This asserts the member list <em>exactly</em>, so adding a field to
/// configuration cannot start flowing to agents, and adding one here is a deliberate change that
/// shows up in review rather than a diff nobody reads.</para>
///
/// <para><b>Why exactly, and not a deny-list.</b> A deny-list of suspicious names only catches the
/// fields someone thought to name suspiciously. The field that would actually have leaked here was
/// called <c>PathOrEndpoint</c>: the locations REST API derives it as
/// <c>Path ?? Endpoint ?? ConnectionString</c> and redacts afterwards, so reusing that helper in
/// the tool would have handed an agent the connection string of every database location under an
/// entirely innocent name. An exact set catches that; a name filter does not.</para>
///
/// <para>The name and type checks below are kept as well, because they explain <em>why</em> a new
/// member failed rather than only that the set changed.</para>
/// </summary>
public sealed class LocationsToolExposureFenceArchitectureTests
{
    /// <summary>
    /// Every member an agent is permitted to see. Adding one means deciding that an agent needs it
    /// and that it cannot help an attacker who has talked their way into the context.
    /// </summary>
    private static readonly string[] ApprovedMembers =
    [
        nameof(LocationEntry.Name),
        nameof(LocationEntry.Type),
        nameof(LocationEntry.Address),
        nameof(LocationEntry.Username),
        nameof(LocationEntry.Description),
        nameof(LocationEntry.Tags),
        nameof(LocationEntry.HasCredential),
        nameof(LocationEntry.VerifyTls),
    ];

    /// <summary>
    /// Substrings that mark a member as credential-bearing. Not the primary guard - the exact set
    /// above is - but it turns "the set changed" into "you added something that looks like a
    /// credential".
    /// </summary>
    private static readonly string[] CredentialNameMarkers =
        ["credential", "connectionstring", "password", "secret", "token", "apikey", "passphrase"];

    private static PropertyInfo[] Members =>
        typeof(LocationEntry).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    [Fact]
    public void LocationEntry_ExposesExactlyTheApprovedMembers()
    {
        var actual = Members.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var approved = ApprovedMembers.OrderBy(n => n, StringComparer.Ordinal).ToList();

        actual.ShouldBe(
            approved,
            "LocationEntry is the exposure boundary for list_locations - the only shape of a "
            + "configured location an agent ever sees. Changing its members changes what every agent "
            + "on the installation can read. If the change is intended, update ApprovedMembers and "
            + "say in review why an agent needs the new field.");
    }

    [Fact]
    public void LocationEntry_HasNoCredentialBearingMember()
    {
        var offenders = Members
            .Where(p => CredentialNameMarkers.Any(marker =>
                p.Name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            // HasCredential reports only whether one is configured - a boolean, never a value.
            .Where(p => !(p.Name == nameof(LocationEntry.HasCredential) && p.PropertyType == typeof(bool)))
            .Select(p => p.Name)
            .ToList();

        offenders.ShouldBeEmpty(
            "These members look credential-bearing. An agent that can read a credential can be "
            + "talked into disclosing it, which is the whole reason this projection exists.");
    }

    /// <summary>
    /// A resolved credential must not be reachable from the projection at all, whatever it is
    /// called.
    /// </summary>
    [Fact]
    public void LocationEntry_CarriesNoSecretTypedMember()
    {
        var offenders = Members
            .Where(p => p.PropertyType == typeof(Secret)
                        || p.PropertyType == typeof(Secret?)
                        || p.PropertyType == typeof(SecretRef)
                        || p.PropertyType == typeof(SecretRef?))
            .Select(p => $"{p.Name}: {p.PropertyType.Name}")
            .ToList();

        offenders.ShouldBeEmpty("A Secret or SecretRef must never be reachable from an agent-facing projection.");
    }

    /// <summary>
    /// HasCredential is the one member whose name is credential-adjacent, and the exemption above
    /// depends on it staying a boolean. If it ever became a string, the exemption would silently
    /// start permitting a value.
    /// </summary>
    [Fact]
    public void HasCredential_IsABooleanNotAValue()
        => typeof(LocationEntry).GetProperty(nameof(LocationEntry.HasCredential))!
            .PropertyType.ShouldBe(typeof(bool));
}
