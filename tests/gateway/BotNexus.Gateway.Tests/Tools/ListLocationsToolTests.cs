using System.Text.Json;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Tools;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class ListLocationsToolTests
{
    private const string ConnectionSecret = "Server=db.internal;User=sa;Password=hunter2";

    private static ListLocationsTool ToolWith(params (string Name, LocationConfig Config)[] locations)
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Locations = locations.ToDictionary(l => l.Name, l => l.Config, StringComparer.OrdinalIgnoreCase)
            }
        };

        return new ListLocationsTool(new StaticOptionsMonitor<PlatformConfig>(config));
    }

    private static async Task<JsonElement> RunAsync(ListLocationsTool tool, Dictionary<string, object?>? args = null)
    {
        var result = await tool.ExecuteAsync("call-1", args ?? new Dictionary<string, object?>(), CancellationToken.None);
        var text = result.Content[0].Value;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static (string, LocationConfig) Proxmox => ("proxmox-main", new LocationConfig
    {
        Type = "remote-node",
        Endpoint = "https://pve.example.lan:8006",
        Username = "automation@pve",
        CredentialRef = "env:PROXMOX_TOKEN",
        Description = "Main hypervisor",
        Tags = ["homelab", "hypervisor"],
        VerifyTls = true
    });

    private static (string, LocationConfig) Database => ("metrics-db", new LocationConfig
    {
        Type = "database",
        ConnectionString = ConnectionSecret,
        Description = "Metrics store"
    });

    // ── The point of the whole design ────────────────────────────────────────────────

    // A database location's connectionString IS the credential. The locations REST API builds its
    // display value as Path ?? Endpoint ?? ConnectionString; reusing that here would have handed
    // every agent the connection string of every database location.
    [Fact]
    public async Task ExecuteAsync_NeverEmitsAConnectionString()
    {
        var tool = ToolWith(Database);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>(), CancellationToken.None);

        result.Content[0].Value.ShouldNotContain(ConnectionSecret);
        result.Content[0].Value.ShouldNotContain("hunter2");
        result.Content[0].Value.ShouldNotContain("Password");
    }

    [Fact]
    public async Task ExecuteAsync_NeverEmitsACredentialReference()
    {
        var tool = ToolWith(Proxmox);

        var result = await tool.ExecuteAsync("call-1", new Dictionary<string, object?>(), CancellationToken.None);

        result.Content[0].Value.ShouldNotContain("PROXMOX_TOKEN");
        result.Content[0].Value.ShouldNotContain("credentialRef", Case.Insensitive);
    }

    // Knowing a target is authenticated is useful; knowing which credential is not.
    [Fact]
    public async Task ExecuteAsync_ReportsThatACredentialExistsWithoutNamingIt()
    {
        var json = await RunAsync(ToolWith(Proxmox, Database));

        foreach (var entry in json.EnumerateArray())
            entry.GetProperty("hasCredential").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ReportsNoCredentialWhenNoneIsConfigured()
    {
        var json = await RunAsync(ToolWith(("docs", new LocationConfig { Type = "filesystem", Path = "/srv/docs" })));

        json[0].GetProperty("hasCredential").GetBoolean().ShouldBeFalse();
    }

    // ── The projection ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ProjectsTheAgentVisibleFields()
    {
        var json = await RunAsync(ToolWith(Proxmox));

        var entry = json[0];
        entry.GetProperty("name").GetString().ShouldBe("proxmox-main");
        entry.GetProperty("type").GetString().ShouldBe("remote-node");
        entry.GetProperty("address").GetString().ShouldBe("https://pve.example.lan:8006");
        entry.GetProperty("username").GetString().ShouldBe("automation@pve");
        entry.GetProperty("description").GetString().ShouldBe("Main hypervisor");
        entry.GetProperty("verifyTls").GetBoolean().ShouldBeTrue();
        entry.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ShouldBe(["homelab", "hypervisor"]);
    }

    // Path for filesystem, endpoint for everything else - and never the third fallback.
    [Fact]
    public async Task ExecuteAsync_UsesPathForFilesystemLocations()
    {
        var json = await RunAsync(ToolWith(("docs", new LocationConfig { Type = "filesystem", Path = "/srv/docs" })));

        json[0].GetProperty("address").GetString().ShouldBe("/srv/docs");
    }

    [Fact]
    public async Task ExecuteAsync_DatabaseLocationHasNoAddress()
    {
        var json = await RunAsync(ToolWith(Database));

        // Nothing safe to show: the only address a database location has is its credential.
        json[0].TryGetProperty("address", out _).ShouldBeFalse();
    }

    // ── Filtering and shape ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NoLocationsConfigured_ReturnsAnEmptyArray()
    {
        var json = await RunAsync(ToolWith());

        json.ValueKind.ShouldBe(JsonValueKind.Array);
        json.GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_FiltersByType()
    {
        var json = await RunAsync(ToolWith(Proxmox, Database), new Dictionary<string, object?> { ["type"] = "database" });

        json.GetArrayLength().ShouldBe(1);
        json[0].GetProperty("name").GetString().ShouldBe("metrics-db");
    }

    [Theory]
    [InlineData("hypervisor")]   // matches a tag
    [InlineData("Main hyper")]   // matches the description
    [InlineData("proxmox")]      // matches the name
    public async Task ExecuteAsync_FiltersByFreeText(string filter)
    {
        var json = await RunAsync(ToolWith(Proxmox, Database), new Dictionary<string, object?> { ["filter"] = filter });

        json.GetArrayLength().ShouldBe(1);
        json[0].GetProperty("name").GetString().ShouldBe("proxmox-main");
    }

    [Fact]
    public async Task ExecuteAsync_OrdersByNameSoOutputIsStable()
    {
        var json = await RunAsync(ToolWith(Proxmox, Database));

        json.EnumerateArray().Select(e => e.GetProperty("name").GetString())
            .ShouldBe(["metrics-db", "proxmox-main"]);
    }

    [Fact]
    public void Definition_DeclaresTheToolByName()
    {
        var tool = ToolWith();

        tool.Name.ShouldBe("list_locations");
        tool.Definition.Name.ShouldBe("list_locations");
        tool.ContentSource.ShouldBe("local");
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
