using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using NKeeper.Abstractions;

namespace NKeeper.Plugins.NetworkScan;

/// <summary>A machine a network scan found, and what it learned about it.</summary>
/// <param name="Names">Each name with where it came from, e.g. <c>nas.lan (reverse DNS)</c>.</param>
/// <param name="RespondedTo">What it answered: ping, tcp or arp.</param>
public sealed record DiscoveredDevice(
    string IpAddress,
    string? MacAddress,
    string? Vendor,
    bool IsVirtual,
    string? Hostname,
    IReadOnlyList<string> Names,
    string? OperatingSystem,
    int? Ttl,
    IReadOnlyList<string> RespondedTo,
    IReadOnlyList<ScannedPort> Ports,
    IReadOnlyList<string> Details);

/// <summary>A network scan, as its dialog polls it.</summary>
/// <param name="State">running, finished, cancelled or failed.</param>
/// <param name="Total">How many addresses it probes.</param>
public sealed record NetworkScanView(
    string Id,
    string Subnet,
    string State,
    int Total,
    int Done,
    IReadOnlyList<DiscoveredDevice> Devices,
    IReadOnlyList<string> Notes,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? Message);

/// <summary>
/// The network scans running on the server (docs/architecture.md §7.42), ported from 1.x's <c>NetworkScans</c>. Only
/// one runs at a time: a sweep is loud enough without several overlapping. Each is visible only to the administrator who
/// started it, and is forgotten two hours after it ends.
/// </summary>
public sealed class NetworkScans(ILogger<NetworkScans> logger, TimeProvider time)
{
    private static readonly TimeSpan KeepFinished = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<string, NetworkScanJob> _jobs = new(StringComparer.Ordinal);
    private readonly Lock _startLock = new();

    /// <returns>The started scan; null when one is already running.</returns>
    internal NetworkScanJob? Start(Guid userId, Subnet subnet)
    {
        lock (_startLock)
        {
            foreach (var old in _jobs.Values.Where(j => j.FinishedAtUtc is { } f && time.GetUtcNow() - f > KeepFinished))
            {
                if (_jobs.TryRemove(old.Id, out var removed))
                {
                    removed.Dispose();
                }
            }

            if (_jobs.Values.Any(j => j.FinishedAtUtc is null))
            {
                return null;
            }

            var job = new NetworkScanJob(userId, subnet, logger, time);
            _jobs[job.Id] = job;
            _ = Task.Run(job.RunAsync);
            return job;
        }
    }

    /// <summary>The user's scan with this ID; null for another user's, or none.</summary>
    internal NetworkScanJob? Find(Guid userId, string id) => _jobs.TryGetValue(id, out var job) && job.UserId == userId ? job : null;

    /// <summary>The user's most recent scan (running or not), so a reopened dialog can pick it up again.</summary>
    internal NetworkScanJob? Latest(Guid userId) => _jobs.Values.Where(j => j.UserId == userId).MaxBy(j => j.StartedAtUtc);
}

/// <summary>
/// Sweeps every address in the subnet, as 1.x's <c>NetworkScanJob</c>. An address counts as a machine when it answers a
/// ping, answers (or refuses) a TCP connection on a common port, or resolves in ARP. Each machine then has its ports
/// scanned and its services asked who they are. The dialog matches them to NKeeper's hosts.
/// </summary>
internal sealed class NetworkScanJob(Guid userId, Subnet subnet, ILogger logger, TimeProvider time) : IDisposable
{
    /// <summary>Addresses probed at once.</summary>
    private const int AddressParallelism = 32;

    /// <summary>Connections open at once, across the whole scan.</summary>
    private const int SocketParallelism = 256;

    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PortTimeout = TimeSpan.FromSeconds(1.5);

    /// <summary>Ports that find a machine that ignores ping: most machines listen on, or refuse, one of these.</summary>
    private static readonly int[] DiscoveryPorts = [22, 80, 135, 139, 443, 445, 3389, 8080];

    private static readonly HashSet<int> HttpPorts = [80, 3000, 5000, 8000, 8008, 8080, 8081, 8888, 9000];
    private static readonly HashSet<int> HttpsPorts = [443, 5001, 6443, 8006, 8443, 9090, 9443, 10000];

    /// <summary>Web ports asked for their page per machine; more are only listed.</summary>
    private const int MaxWebPorts = 4;

    private readonly CancellationTokenSource _cancel = new();
    private readonly SemaphoreSlim _sockets = new(SocketParallelism);
    private readonly ConcurrentBag<DiscoveredDevice> _devices = [];
    private readonly ConcurrentBag<string> _notes = [];
    private int _done;
    private int _pinged;
    private string _state = "running";
    private string? _message;

    public string Id { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    public Guid UserId { get; } = userId;

    public DateTimeOffset StartedAtUtc { get; } = time.GetUtcNow();

    public DateTimeOffset? FinishedAtUtc { get; private set; }

    public Subnet Subnet { get; } = subnet;

    public void Cancel() => _cancel.Cancel();

    /// <summary>Called once the finished scan is forgotten.</summary>
    public void Dispose()
    {
        _cancel.Dispose();
        _sockets.Dispose();
    }

    public NetworkScanView View() => new(
        Id,
        Subnet.ToString(),
        _state,
        Subnet.Count,
        Volatile.Read(ref _done),
        [.. _devices.OrderBy(d => Subnet.ToUInt(IPAddress.Parse(d.IpAddress)))],
        [.. _notes.Distinct().Order(StringComparer.Ordinal)],
        StartedAtUtc,
        FinishedAtUtc,
        _message);

    public async Task RunAsync()
    {
        logger.LogInformation("Network scan {ScanId} of {Subnet} started ({Count} addresses)", Id, Subnet, Subnet.Count);
        var token = _cancel.Token;
        try
        {
            AddLocationNotes();
            await Parallel.ForEachAsync(
                Subnet.Addresses(),
                new ParallelOptions { MaxDegreeOfParallelism = AddressParallelism, CancellationToken = token },
                async (address, ct) =>
                {
                    try
                    {
                        if (await ProbeAsync(address, ct) is { } device)
                        {
                            _devices.Add(device);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Network scan {ScanId}: probing {Address} failed", Id, address);
                    }
                    finally
                    {
                        Interlocked.Increment(ref _done);
                    }
                });

            if (!_devices.IsEmpty && _pinged == 0)
            {
                _notes.Add("Nothing answered a ping: ICMP may be blocked on this network, or not available to the NKeeper server. Machines were found by TCP and ARP instead, so ones that drop all of those were missed.");
            }

            _state = "finished";
        }
        catch (OperationCanceledException)
        {
            _state = "cancelled";
        }
#pragma warning disable CA1031 // The scan runs unattended: any failure is reported in its state.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogError(ex, "Network scan {ScanId} failed", Id);
            _state = "failed";
            _message = "The scan stopped unexpectedly. The server's diagnostics log has the details.";
        }
        finally
        {
            FinishedAtUtc = time.GetUtcNow();
            logger.LogInformation("Network scan {ScanId} of {Subnet} {State}: {Found} machines in {Done} addresses", Id, Subnet, _state, _devices.Count, _done);
        }
    }

    private void AddLocationNotes()
    {
        var local = Subnet.Local();
        var onSegment = local.Any(l => (Subnet.ToUInt(IPAddress.Parse(l.Address)) & (uint.MaxValue << (32 - Subnet.Prefix))) == Subnet.Network
            || (Subnet.Network & (uint.MaxValue << (32 - l.Subnet.Prefix))) == l.Subnet.Network);
        if (!onSegment)
        {
            _notes.Add("The NKeeper server isn't on this network, so MAC addresses can't be seen through the router (Windows and Samba machines report theirs over NetBIOS), and machines that only answer ARP are missed.");
        }

        if (File.Exists("/.dockerenv") || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
        {
            _notes.Add("NKeeper runs in a container: unless it uses the host's network, other machines' MAC addresses aren't visible to it.");
        }
    }

    private async Task<DiscoveredDevice?> ProbeAsync(IPAddress address, CancellationToken ct)
    {
        var responded = new List<string>();
        var open = new SortedSet<int>();
        var (pinged, ttl) = await NetworkProbes.PingAsync(address, PingTimeout, ct);
        if (pinged)
        {
            responded.Add("ping");
            Interlocked.Increment(ref _pinged);
        }

        var probed = new HashSet<int>();
        if (!pinged)
        {
            var answers = await Task.WhenAll(DiscoveryPorts.Select(async port => (port, answer: await ConnectAsync(address, port, DiscoveryTimeout, ct))));
            probed.UnionWith(DiscoveryPorts);
            open.UnionWith(answers.Where(a => a.answer == TcpAnswer.Open).Select(a => a.port));
            if (answers.Any(a => a.answer != TcpAnswer.Silent))
            {
                responded.Add("tcp");
            }
        }

        // After the probes above, a machine on the server's segment is in its ARP table even when it answers nothing else.
        var mac = await Task.Run(() => NeighborTable.FindMac(address), ct);
        if (mac is not null)
        {
            responded.Add("arp");
        }

        if (responded.Count == 0)
        {
            return null;
        }

        var rest = ScanPorts.Network.Where(p => !probed.Contains(p)).ToArray();
        var results = await Task.WhenAll(rest.Select(async port => (port, answer: await ConnectAsync(address, port, PortTimeout, ct))));
        open.UnionWith(results.Where(r => r.answer == TcpAnswer.Open).Select(r => r.port));
        if (!responded.Contains("tcp") && results.Any(r => r.answer != TcpAnswer.Silent))
        {
            responded.Add("tcp");
        }

        return await DescribeAsync(address, mac, ttl, responded, open, ct);
    }

    /// <summary>Asks the machine's services who they are, and puts together what it is.</summary>
    private static async Task<DiscoveredDevice> DescribeAsync(IPAddress address, string? mac, int? ttl, List<string> responded, SortedSet<int> open, CancellationToken ct)
    {
        var details = new List<string>();
        var names = new List<(string Name, string Source)>();

        var dnsTask = NetworkProbes.ReverseDnsAsync(address, ct);
        var netBiosTask = NetworkProbes.NetBiosAsync(address, ct);
        var sshTask = open.Contains(22) ? NetworkProbes.SshBannerAsync(address, 22, ct) : Task.FromResult<string?>(null);
        var webPorts = open.Where(p => HttpPorts.Contains(p) || HttpsPorts.Contains(p)).Take(MaxWebPorts).ToList();
        var webTasks = webPorts.Select(p => NetworkProbes.WebAsync(address, p, HttpsPorts.Contains(p), ct)).ToList();
        await Task.WhenAll([dnsTask, netBiosTask, sshTask, .. webTasks]);

        if (await dnsTask is { } dns)
        {
            names.Add((dns, "reverse DNS"));
        }

        var netBios = await netBiosTask;
        if (netBios is not null)
        {
            if (netBios.Name is { } nb)
            {
                names.Add((nb, "NetBIOS"));
            }

            details.Add($"NetBIOS: {netBios.Name ?? "no name"}{(netBios.Workgroup is { } wg ? $" in workgroup or domain {wg}" : "")}");
            mac ??= netBios.MacAddress;
        }

        string? os = null;
        if (await sshTask is { } banner)
        {
            details.Add($"SSH: {banner}");
            os = ExternalScan.OsFromSshBanner(banner);
        }

        for (var i = 0; i < webPorts.Count; i++)
        {
            if (await webTasks[i] is not { } web)
            {
                continue;
            }

            var parts = new List<string> { $"{web.Status}" };
            if (web.Server is { } server)
            {
                parts.Add($"server {server}");
            }

            if (web.Title is { } title)
            {
                parts.Add($"“{title}”");
            }

            if (web.Location is { } location)
            {
                parts.Add($"redirects to {location}");
            }

            details.Add($"Web on port {webPorts[i]}: {string.Join(" · ", parts)}");
            if (web.CertificateSubject is { } subject)
            {
                var alternatives = web.CertificateNames.Where(n => !n.Equals(subject, StringComparison.OrdinalIgnoreCase)).ToList();
                details.Add($"Certificate on port {webPorts[i]}: {subject}{(alternatives.Count > 0 ? $" (also {string.Join(", ", alternatives.Take(5))})" : "")}");
            }

            foreach (var name in web.CertificateNames.Where(NetworkProbes.LooksLikeHostname))
            {
                names.Add((name, $"certificate on port {webPorts[i]}"));
            }
        }

        if (os is null && (open.Contains(3389) || open.Contains(135) || (netBios is not null && open.Contains(445))))
        {
            os = "Windows";
        }

        if (NetworkProbes.TtlHint(ttl) is { } hint)
        {
            details.Add($"Ping TTL {ttl}: suggests {hint}");
        }

        var (vendor, isVirtual) = NetworkProbes.MacVendor(mac);
        var uniqueNames = names.DistinctBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new DiscoveredDevice(
            address.ToString(),
            mac,
            vendor,
            isVirtual,
            uniqueNames.FirstOrDefault().Name,
            [.. uniqueNames.Select(n => $"{n.Name} ({n.Source})")],
            os,
            ttl,
            responded,
            [.. open.Select(ScanPorts.Describe)],
            details);
    }

    private async Task<TcpAnswer> ConnectAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken ct)
    {
        await _sockets.WaitAsync(ct);
        try
        {
            return await NetworkProbes.ConnectAsync(address, port, timeout, ct);
        }
        finally
        {
            _sockets.Release();
        }
    }
}
