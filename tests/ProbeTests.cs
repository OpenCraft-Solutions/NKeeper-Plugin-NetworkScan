using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NKeeper.Plugins.NetworkScan.Tests;

/// <summary>Ported from 1.x's NetworkScanLogicTests and HostScanTests: the subnet, the probes' parsers and the neighbour table.</summary>
public sealed class ProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.0/24", 254)]
    [InlineData("192.168.1.77/24", "192.168.1.0/24", 254)]
    [InlineData(" 10.0.0.5 ", "10.0.0.5/32", 1)]
    [InlineData("10.0.0.4/31", "10.0.0.4/31", 2)]
    [InlineData("10.0.16.0/20", "10.0.16.0/20", 4094)]
    public void Subnet_Parses_ToItsNetwork(string text, string expected, int count)
    {
        Assert.True(Subnet.TryParse(text, out var subnet, out var error), error);
        Assert.Equal(expected, subnet.ToString());
        Assert.Equal(count, subnet.Count);
        Assert.Equal(count, subnet.Addresses().Count());
    }

    [Theory]
    [InlineData("10.0.0.0/16", "at most 4,096")]
    [InlineData("10.0.0.0/33", "prefix length")]
    [InlineData("server01/24", "CIDR")]
    [InlineData("10.1/24", "CIDR")]
    [InlineData("fe80::/120", "IPv4")]
    public void Subnet_RefusesWhatItCantSweep(string text, string reason)
    {
        Assert.False(Subnet.TryParse(text, out _, out var error));
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Subnet_LeavesOutTheNetworkAndBroadcastAddresses()
    {
        Assert.True(Subnet.TryParse("10.0.0.0/30", out var subnet, out _));
        Assert.Equal(["10.0.0.1", "10.0.0.2"], subnet.Addresses().Select(a => a.ToString()));
    }

    [Fact]
    public void NetBios_ReadsTheNameWorkgroupAndMac()
    {
        const ushort transaction = 0x1234;
        var request = NetworkProbes.NodeStatusRequest(transaction);
        Assert.Equal(50, request.Length);
        Assert.Equal("CKAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", Encoding.ASCII.GetString(request, 13, 32));

        // The answer echoes the question's name, then lists the machine's names and its MAC address.
        var response = new List<byte>
        {
            0x12, 0x34, 0x84, 0x00, 0, 0, 0, 1, 0, 0, 0, 0,
        };
        response.AddRange(request.Skip(12).Take(34)); // the name
        response.AddRange([0, 0x21, 0, 1, 0, 0, 0, 0, 0, 0]); // type, class, TTL, data length (unchecked)
        response.Add(3);
        response.AddRange(Entry("FILESRV01", 0x00, group: false));
        response.AddRange(Entry("CORP", 0x00, group: true));
        response.AddRange(Entry("FILESRV01", 0x20, group: false));
        response.AddRange([0x00, 0x15, 0x5D, 0x01, 0x02, 0x03]);

        var answer = NetworkProbes.ParseNodeStatus([.. response], transaction);

        Assert.Equal(new NetworkProbes.NetBiosAnswer("FILESRV01", "CORP", "00:15:5D:01:02:03"), answer);
        Assert.Null(NetworkProbes.ParseNodeStatus([.. response], 0x9999));

        static byte[] Entry(string name, byte suffix, bool group) =>
            [.. Encoding.ASCII.GetBytes(name.PadRight(15)), suffix, (byte)(group ? 0x84 : 0x04), 0x00];
    }

    [Theory]
    [InlineData("00:50:56:AA:BB:CC", "VMware", true)]
    [InlineData("bc:24:11:aa:bb:cc", "Proxmox VE", true)]
    [InlineData("02:42:AC:11:00:02", "Docker container", true)]
    [InlineData("B8:27:EB:00:00:01", "Raspberry Pi", false)]
    [InlineData("DA:A1:19:00:00:01", "Randomized or locally administered", false)]
    [InlineData("00:1B:21:00:00:01", null, false)]
    public void MacVendor_NamesKnownPrefixes(string mac, string? vendor, bool isVirtual) =>
        Assert.Equal((vendor, isVirtual), NetworkProbes.MacVendor(mac));

    [Fact]
    public void Probes_ReadTitles_AndTellHostnamesFromOtherNames()
    {
        Assert.Equal("Proxmox Virtual Environment", NetworkProbes.TitleOf("<html><head><TITLE>\n Proxmox  Virtual Environment </TITLE>"));
        Assert.Equal("Tom & Jerry", NetworkProbes.TitleOf("<title>Tom &amp; Jerry</title>"));
        Assert.Null(NetworkProbes.TitleOf("<html><body>no title</body></html>"));

        Assert.True(NetworkProbes.LooksLikeHostname("pve01.lab.example"));
        Assert.False(NetworkProbes.LooksLikeHostname("*.lab.example"));
        Assert.False(NetworkProbes.LooksLikeHostname("10.0.0.5"));
        Assert.False(NetworkProbes.LooksLikeHostname("localhost.localdomain"));

        Assert.Equal("Windows", NetworkProbes.TtlHint(127));
        Assert.Null(NetworkProbes.TtlHint(null));
    }

    [Fact]
    public async Task WebProbe_ReadsTheServerAndTitle()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync(Ct);
            await socket.ReceiveAsync(new byte[4096], Ct);
            const string body = "<html><head><title>Printer status</title></head></html>";
            await socket.SendAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nServer: TestPrinter/2.1\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), Ct);
        }, Ct);

        var answer = await NetworkProbes.WebAsync(IPAddress.Loopback, port, https: false, Ct);
        await serve;

        Assert.NotNull(answer);
        Assert.Equal(200, answer.Status);
        Assert.Equal("TestPrinter/2.1", answer.Server);
        Assert.Equal("Printer status", answer.Title);
    }

    [Theory]
    [InlineData("SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13.5", "Ubuntu")]
    [InlineData("SSH-2.0-OpenSSH_9.2p1 Debian-2+deb12u3", "Debian")]
    [InlineData("SSH-2.0-OpenSSH_for_Windows_9.5", "Windows")]
    [InlineData("SSH-2.0-OpenSSH_9.8", null)]
    public void OsIsGuessedFromSshBanners(string banner, string? os) => Assert.Equal(os, ExternalScan.OsFromSshBanner(banner));

    [Fact]
    public void MacAddresses_AreReadFromTheArpTable()
    {
        string[] arp =
        [
            "IP address       HW type     Flags       HW address            Mask     Device",
            "10.0.0.1         0x1         0x2         aa:bb:cc:00:00:01     *        eth0",
            "10.0.0.7         0x1         0x0         00:00:00:00:00:00     *        eth0",
        ];

        Assert.Equal("AA:BB:CC:00:00:01", NeighborTable.FromProcArp(arp, IPAddress.Parse("10.0.0.1")));
        Assert.Null(NeighborTable.FromProcArp(arp, IPAddress.Parse("10.0.0.7")));
    }
}
