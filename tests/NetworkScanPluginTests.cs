using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using NKeeper.Testing;
using Npgsql;

namespace NKeeper.Plugins.NetworkScan.Tests;

/// <summary>
/// docs/architecture.md §7.42: the real <c>network-scan</c> plugin, packaged by its build and loaded into NKeeper's test
/// server. Its scans probe the loopback address only, where the test opens a port of its own.
/// </summary>
public sealed class NetworkScanPluginTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private TestServer _server = null!;
    private string _connectionString = string.Empty;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync();
        var options = new TestServerOptions { ConnectionString = _connectionString };
        foreach (var package in DevelopmentKeys.BuiltPackages())
        {
            options.PluginPackages.Add(package);
        }

        _server = await TestServer.StartAsync(options, Token);
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task The_plugin_contributes_a_host_wide_scanner()
    {
        using var admin = await _server.SignedInClientAsync(Token);
        var plugins = await admin.Http.GetFromJsonAsync<JsonElement>(new Uri("/api/plugins", UriKind.Relative), Token);
        var plugin = plugins.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "network-scan");
        Assert.Equal("loaded", plugin.GetProperty("state").GetString());
        var action = Assert.Single(plugin.GetProperty("hostActions").EnumerateArray());
        Assert.Equal(
            ("network-scan.host", true, "none", 0),
            (action.GetProperty("id").GetString(), action.GetProperty("scan").GetBoolean(), action.GetProperty("credential").GetString(), action.GetProperty("serviceTypes").GetArrayLength()));
    }

    [Fact]
    public async Task The_external_scan_finds_an_open_common_port_on_the_hosts_own_address_and_is_audited()
    {
        // 9100 is one of the common ports. If it's taken, something else listens there, which is open just as well.
        using var listener = new TcpListener(IPAddress.Loopback, Port);
        TryStart(listener);
        using var admin = await _server.SignedInClientAsync(Token);
        var host = await CreateHostAsync(admin, "loop", "127.0.0.1");

        using var scanned = await admin.SendAsync(HttpMethod.Post, $"/api/p/network-scan/hosts/{Id(host)}/external", new { }, Token);
        Assert.True(scanned.IsSuccessStatusCode, await scanned.Content.ReadAsStringAsync(Token));
        var findings = await scanned.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal("127.0.0.1", findings.GetProperty("ipAddress").GetString());
        Assert.Contains(Port, findings.GetProperty("ports").EnumerateArray().Select(p => p.GetProperty("port").GetInt32()));
        Assert.Single(await AuditAsync("network-scan.host"));
    }

    [Fact]
    public async Task A_network_scan_must_be_acknowledged_and_small_enough_and_is_for_administrators()
    {
        using var admin = await _server.SignedInClientAsync(Token);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, "/api/p/network-scan/admin/scans", new { subnet = "127.0.0.1/32", acknowledged = false }, Token)).StatusCode);
        using (var large = await admin.SendAsync(HttpMethod.Post, "/api/p/network-scan/admin/scans", new { subnet = "10.0.0.0/16", acknowledged = true }, Token))
        {
            Assert.Equal(HttpStatusCode.BadRequest, large.StatusCode);
            Assert.Contains("at most 4,096", await large.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        }

        using (var created = await admin.SendAsync(HttpMethod.Post, "/api/users", new { userName = "bob", email = "bob@nkeeper.test", password = "Bob-Passw0rd-123", role = "user", mustChangePassword = false }, Token))
        {
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(Token));
        }

        using var bob = _server.CreateClient();
        (await bob.SendAsync(HttpMethod.Post, "/api/auth/login", new { userNameOrEmail = "bob", password = "Bob-Passw0rd-123", rememberMe = false }, Token)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.SendAsync(HttpMethod.Post, "/api/p/network-scan/admin/scans", new { subnet = "127.0.0.1/32", acknowledged = true }, Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.Http.GetAsync(new Uri("/api/p/network-scan/admin/networks", UriKind.Relative), Token)).StatusCode);
    }

    [Fact]
    public async Task A_network_scan_of_loopback_finds_the_machine_and_is_audited()
    {
        using var listener = new TcpListener(IPAddress.Loopback, Port);
        TryStart(listener);
        using var admin = await _server.SignedInClientAsync(Token);

        using var started = await admin.SendAsync(HttpMethod.Post, "/api/p/network-scan/admin/scans", new { subnet = "127.0.0.1", acknowledged = true }, Token);
        Assert.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync(Token));
        var scan = await started.Content.ReadFromJsonAsync<JsonElement>(Token);
        var id = scan.GetProperty("id").GetString();
        if (scan.GetProperty("state").GetString() == "running")
        {
            // One at a time.
            Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(HttpMethod.Post, "/api/p/network-scan/admin/scans", new { subnet = "127.0.0.1", acknowledged = true }, Token)).StatusCode);
        }

        for (var i = 0; i < 120 && scan.GetProperty("state").GetString() == "running"; i++)
        {
            await Task.Delay(500, Token);
            scan = await admin.Http.GetFromJsonAsync<JsonElement>(new Uri($"/api/p/network-scan/admin/scans/{id}", UriKind.Relative), Token);
        }

        Assert.Equal("finished", scan.GetProperty("state").GetString());
        var device = Assert.Single(scan.GetProperty("devices").EnumerateArray());
        Assert.Equal("127.0.0.1", device.GetProperty("ipAddress").GetString());
        Assert.Contains(Port, device.GetProperty("ports").EnumerateArray().Select(p => p.GetProperty("port").GetInt32()));

        Assert.Equal(id, (await admin.Http.GetFromJsonAsync<JsonElement>(new Uri("/api/p/network-scan/admin/scans/latest", UriKind.Relative), Token)).GetProperty("id").GetString());
        Assert.Equal(["127.0.0.1/32 · 1 addresses"], await AuditAsync("network-scan.subnet"));
    }

    private const int Port = 9100;

    /// <summary>The port may already be taken, by something that listens on it, which is open just as well.</summary>
    private static bool TryStart(TcpListener listener)
    {
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task<JsonElement> CreateHostAsync(TestServerClient admin, string name, string ipAddress)
    {
        using var created = await admin.SendAsync(HttpMethod.Post, "/api/hosts", new { name, ipAddress }, Token);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Token));
        return await created.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private static Guid Id(JsonElement host) => host.GetProperty("id").GetGuid();

    private async Task<List<string>> AuditAsync(string action)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand("SELECT detail FROM core.audit_events WHERE action = @action ORDER BY id", connection);
        command.Parameters.AddWithValue("action", action);
        await using var reader = await command.ExecuteReaderAsync(Token);
        var details = new List<string>();
        while (await reader.ReadAsync(Token))
        {
            details.Add(reader.GetString(0));
        }

        return details;
    }
}
