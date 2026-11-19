using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NKeeper.Abstractions;

namespace NKeeper.Plugins.NetworkScan;

/// <summary>The host's address couldn't be found, or other scans are running.</summary>
internal sealed class ScanRefusedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// An external scan (docs/architecture.md §7.42), ported from 1.x's <c>ExternalScan</c>: from the NKeeper server over
/// the network, TCP connections to common ports and the host's own service ports on the host's own address, the SSH
/// banner, reverse DNS, and the MAC address when the host shares the server's network segment. Only the host's own
/// address is ever probed. It's visible on the network as a port scan.
/// </summary>
internal sealed class ExternalScan(ILogger<ExternalScan> logger)
{
    private const int Parallelism = 32;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(3);

    /// <summary>At most a few run on the server at a time.</summary>
    private static readonly SemaphoreSlim Running = new(4);

    /// <exception cref="ScanRefusedException">The address can't be resolved, or other scans are running.</exception>
    public async Task<ScanFindings> ScanAsync(HostContext host, CancellationToken cancellationToken)
    {
        if (!await Running.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken))
        {
            throw new ScanRefusedException("scans_busy", "Other scans are running. Try again in a moment.");
        }

        try
        {
            return await RunAsync(host, cancellationToken);
        }
        finally
        {
            Running.Release();
        }
    }

    private async Task<ScanFindings> RunAsync(HostContext host, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var address = await ResolveAsync(host, cancellationToken);

        var ports = ScanPorts.Common.Concat(host.Services.Select(s => s.Port).OfType<int>()).Distinct().Order().ToList();
        var open = new List<int>();
        await Parallel.ForEachAsync(
            ports,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (port, token) =>
            {
                if (await NetworkProbes.ConnectAsync(address, port, ConnectTimeout, token) == TcpAnswer.Open)
                {
                    lock (open)
                    {
                        open.Add(port);
                    }
                }
            });
        open.Sort();
        logger.LogInformation("External scan of host {HostId} ({Address}): {Open} of {Probed} ports open", host.Id, address, open.Count, ports.Count);

        string? os = null;
        var sshPorts = host.Services.Where(s => s.Type == "ssh").Select(s => s.Port ?? 22).Append(22);
        foreach (var port in sshPorts.Distinct().Where(open.Contains))
        {
            if (await NetworkProbes.SshBannerAsync(address, port, cancellationToken) is { } banner)
            {
                notes.Add($"SSH on port {port} says: {banner}");
                os ??= OsFromSshBanner(banner);
            }
        }

        if (os is null && (open.Contains(3389) || open.Contains(445) || open.Contains(135)))
        {
            os = "Windows";
            notes.Add("The operating system is a guess from the open ports (RDP, SMB or Microsoft RPC).");
        }
        else if (os is not null)
        {
            notes.Add("The operating system is a guess from the SSH banner.");
        }

        var mac = NeighborTable.FindMac(address);
        if (mac is null)
        {
            notes.Add("No MAC address: it's only visible for hosts on the same network segment as the NKeeper server (not through a router, a VPN or a container network).");
        }

        var hostname = await NetworkProbes.ReverseDnsAsync(address, cancellationToken);
        if (open.Count == 0)
        {
            notes.Add("No open ports were found. The host may be down, or a firewall drops connections.");
        }

        return new ScanFindings
        {
            Hostname = hostname,
            IpAddress = address.ToString(),
            MacAddress = mac,
            OperatingSystem = os,
            Ports = [.. open.Select(ScanPorts.Describe)],
            Notes = notes,
        };
    }

    /// <summary>The host's IP address, or its hostname resolved once (IPv4 first).</summary>
    private static async Task<IPAddress> ResolveAsync(HostContext host, CancellationToken cancellationToken)
    {
        if (host.IpAddress is { } ip && IPAddress.TryParse(ip, out var parsed))
        {
            return parsed;
        }

        if (string.IsNullOrWhiteSpace(host.Hostname))
        {
            throw new ScanRefusedException("no_address", "The host has no IP address or hostname to scan.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DnsTimeout);
            var addresses = await Dns.GetHostAddressesAsync(host.Hostname, timeout.Token);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.First();
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new ScanRefusedException("no_address", $"“{host.Hostname}” couldn't be resolved to an address.");
        }
    }

    internal static string? OsFromSshBanner(string banner)
    {
        string[] known = ["Ubuntu", "Debian", "Raspbian", "FreeBSD", "OpenBSD", "NetBSD"];
        return banner.Contains("OpenSSH_for_Windows", StringComparison.OrdinalIgnoreCase)
            ? "Windows"
            : known.FirstOrDefault(name => banner.Contains(name, StringComparison.OrdinalIgnoreCase));
    }
}
