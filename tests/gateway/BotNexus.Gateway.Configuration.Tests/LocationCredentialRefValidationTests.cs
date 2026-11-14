using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// A location's <c>credentialRef</c> holds a pointer to a credential, never the credential. These
/// pin the rejection, because the whole value of the field is that the wrong thing cannot go in it.
/// </summary>
public sealed class LocationCredentialRefValidationTests
{
    private static PlatformConfig ConfigWith(string? credentialRef) => new()
    {
        Gateway = new GatewaySettingsConfig
        {
            Locations = new Dictionary<string, LocationConfig>
            {
                ["proxmox-main"] = new()
                {
                    Type = "remote-node",
                    Endpoint = "https://pve.example.lan:8006",
                    Username = "automation@pve",
                    CredentialRef = credentialRef
                }
            }
        }
    };

    // The Phase 2 acceptance criterion: a literal fails validation, and the error names the key.
    [Fact]
    public void InlineCredential_FailsValidation_NamingTheKey()
    {
        var errors = PlatformConfigValidator.Validate(ConfigWith("hunter2"));

        var error = errors.ShouldHaveSingleItem();
        error.ShouldContain("gateway.locations.proxmox-main.credentialRef");
        error.ShouldContain("scheme:identifier");
    }

    // If it really was a pasted credential, this error may well reach a log.
    [Fact]
    public void InlineCredential_ErrorDoesNotEchoTheValue()
    {
        const string Pasted = "definitely-a-real-password";

        var errors = PlatformConfigValidator.Validate(ConfigWith(Pasted));

        errors.ShouldNotBeEmpty();
        errors.ShouldAllBe(e => !e.Contains(Pasted));
    }

    [Theory]
    [InlineData("env:PROXMOX_TOKEN")]
    [InlineData("file:~/.botnexus/secrets/proxmox")]
    [InlineData("file:/etc/botnexus/secrets/proxmox")]
    [InlineData("sqlite:proxmox-main")]
    [InlineData("keyring:botnexus/proxmox")]
    public void WellFormedReference_Passes(string reference)
        => PlatformConfigValidator.Validate(ConfigWith(reference)).ShouldBeEmpty();

    // Unset is legitimate: not every location needs a credential.
    [Fact]
    public void AbsentReference_Passes()
        => PlatformConfigValidator.Validate(ConfigWith(null)).ShouldBeEmpty();

    // Present-but-blank is a half-finished edit, not an absent credential, so it is reported.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankReference_FailsValidation(string reference)
    {
        var errors = PlatformConfigValidator.Validate(ConfigWith(reference));

        errors.ShouldNotBeEmpty();
        errors[0].ShouldContain("credentialRef");
    }

    [Fact]
    public void MalformedScheme_FailsValidation()
    {
        var errors = PlatformConfigValidator.Validate(ConfigWith("1234:5678"));

        errors.ShouldNotBeEmpty();
        errors[0].ShouldContain("credentialRef");
    }

    // credentialRef is not tied to one location type - a filesystem location backed by a
    // credentialled mount is as legitimate as an API one.
    [Fact]
    public void AppliesToEveryLocationType()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Locations = new Dictionary<string, LocationConfig>
                {
                    ["files"] = new() { Type = "filesystem", Path = "/srv/data", CredentialRef = "hunter2" }
                }
            }
        };

        var errors = PlatformConfigValidator.Validate(config);

        errors.ShouldContain(e => e.Contains("gateway.locations.files.credentialRef"));
    }

    [Fact]
    public void VerifyTls_DefaultsToVerifying()
        => new LocationConfig().VerifyTls.ShouldBeTrue();
}
