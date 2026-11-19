using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NKeeper.Abstractions;

namespace NKeeper.Plugins.NetworkScan;

public sealed record NetworkScanRequest(string? Subnet, bool Acknowledged);

public sealed record LocalNetwork(string Subnet, string Interface, string Address);

/// <summary>
/// The network-scan plugin (docs/architecture.md §7.42): 1.x's external scan of one host, as a host-wide scanner
/// (SDK 1.14), and its network scan of a subnet, as an administrators' inventory tool. It probes from the NKeeper server
/// with its own sockets, and only the host's own address or the administrator's subnet. Both are audited.
/// </summary>
public sealed class NetworkScanPlugin : IPlugin
{
    public void Contribute(IPluginContributions contributions, PluginContext context) =>
        contributions.AddHostAction(new HostAction("network-scan.host", "From the NKeeper server (port scan)")
        {
            Endpoint = "external",
            Scan = true,
            Credential = HostActionCredential.None,
        });

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton<ExternalScan>();
        services.AddSingleton<NetworkScans>();
        services.TryAddSingleton(TimeProvider.System);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
    {
        // The external scan: someone who can manage the host, its own address only.
        endpoints.MapHostGroup(HostPermission.Manage).MapPost("/external", async (HostContext host, ExternalScan scan, IPluginAudit audit, CancellationToken ct) =>
        {
            try
            {
                var findings = await scan.ScanAsync(host, ct);
                await audit.RecordAsync("network-scan.host", host.Id, $"{findings.IpAddress}: {findings.Ports.Count} open ports", ct);
                return Results.Json(findings);
            }
            catch (ScanRefusedException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
            }
        });

        // The network scan: administrators, one at a time, each seeing only their own.
        var admin = endpoints.MapAdministratorGroup();
        admin.MapGet("/networks", () => Results.Json(Subnet.Local().Select(l => new LocalNetwork(l.Subnet.ToString(), l.Interface, l.Address))));
        admin.MapGet("/scans/latest", (NetworkScans scans, ClaimsPrincipal user) =>
            scans.Latest(UserId(user)) is { } job ? Results.Json(job.View()) : Results.NoContent());
        admin.MapGet("/scans/{id}", (string id, NetworkScans scans, ClaimsPrincipal user) =>
            scans.Find(UserId(user), id) is { } job ? Results.Json(job.View()) : Results.NotFound());
        admin.MapPost("/scans/{id}/cancel", (string id, NetworkScans scans, ClaimsPrincipal user) =>
        {
            if (scans.Find(UserId(user), id) is not { } job)
            {
                return Results.NotFound();
            }

            job.Cancel();
            return Results.Json(job.View());
        });
        admin.MapPost("/scans", async (NetworkScanRequest request, NetworkScans scans, IPluginAudit audit, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (!request.Acknowledged)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["acknowledged"] = ["Confirm that you understand the scan is loud and may set off security tools."] });
            }

            if (!Subnet.TryParse(request.Subnet, out var subnet, out var error))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["subnet"] = [error] });
            }

            if (scans.Start(UserId(user), subnet) is not { } job)
            {
                return Results.Problem(
                    "Only one network scan runs at a time. Wait for it to finish, or cancel it.",
                    statusCode: StatusCodes.Status409Conflict,
                    title: "A network scan is already running",
                    extensions: new Dictionary<string, object?> { ["code"] = "scan_running" });
            }

            await audit.RecordAsync("network-scan.subnet", null, $"{subnet} · {subnet.Count:N0} addresses", ct);
            return Results.Json(job.View());
        });
    }

    /// <summary>The signed-in user, who core has already authenticated for the administrator group.</summary>
    private static Guid UserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
}
