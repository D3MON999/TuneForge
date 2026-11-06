using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

public readonly record struct PingReply(bool Reached, bool Hop, string? Address, double Ms);

public interface INetworkProbe
{
    IPAddress Resolve(string host);

    PingReply Ping(IPAddress address, int ttl, int timeoutMs);

    /// <summary>Connection time in milliseconds, or null if the port didn't answer.</summary>
    double? Connect(IPAddress address, int port, int timeoutMs);

    /// <summary>Time for one DNS lookup, or null if the server didn't answer.</summary>
    double? Lookup(IPAddress server, string domain, int timeoutMs);
}

public sealed class WindowsNetworkProbe : INetworkProbe
{
    private const int DnsPort = 53;
    private static readonly byte[] Payload = new byte[32];

    public IPAddress Resolve(string host)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            return address;
        }

        try
        {
            var addresses = Dns.GetHostAddresses(host);
            return addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.First();
        }
        catch (Exception exception) when (exception is SocketException or InvalidOperationException)
        {
            throw new CatalogRequestException($"{host} couldn't be found.");
        }
    }

    public PingReply Ping(IPAddress address, int ttl, int timeoutMs)
    {
        using var ping = new Ping();
        var clock = Stopwatch.StartNew();
        try
        {
            var reply = ping.Send(address, timeoutMs, Payload, new PingOptions(ttl, true));
            var elapsed = clock.Elapsed.TotalMilliseconds;
            return reply.Status switch
            {
                IPStatus.Success => new PingReply(true, false, reply.Address.ToString(), reply.RoundtripTime > 0 ? reply.RoundtripTime : elapsed),
                IPStatus.TtlExpired => new PingReply(false, true, reply.Address.ToString(), elapsed),
                _ => new PingReply(false, false, null, 0),
            };
        }
        catch (PingException)
        {
            return new PingReply(false, false, null, 0);
        }
    }

    public double? Connect(IPAddress address, int port, int timeoutMs)
    {
        using var client = new TcpClient(address.AddressFamily);
        var clock = Stopwatch.StartNew();
        try
        {
            return client.ConnectAsync(address, port).Wait(timeoutMs) ? clock.Elapsed.TotalMilliseconds : null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }

    public double? Lookup(IPAddress server, string domain, int timeoutMs)
    {
        using var client = new UdpClient(server.AddressFamily);
        client.Client.ReceiveTimeout = timeoutMs;
        var id = (ushort)Random.Shared.Next(ushort.MaxValue);
        var query = DnsPacket.BuildQuery(id, domain);
        var clock = Stopwatch.StartNew();
        try
        {
            client.Send(query, query.Length, new IPEndPoint(server, DnsPort));
            var remote = new IPEndPoint(IPAddress.Any, 0);
            return DnsPacket.IsAnswerTo(id, client.Receive(ref remote)) ? clock.Elapsed.TotalMilliseconds : null;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}

/// <summary>The small part of the DNS message format needed to time one lookup.</summary>
public static class DnsPacket
{
    private const int HeaderSize = 12;

    public static byte[] BuildQuery(ushort id, string domain)
    {
        var bytes = new List<byte>
        {
            (byte)(id >> 8), (byte)id,
            0x01, 0x00, // Ask the server to look it up fully.
            0x00, 0x01, // One question.
            0, 0, 0, 0, 0, 0,
        };
        foreach (var label in domain.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }

        bytes.AddRange([0, 0x00, 0x01, 0x00, 0x01]); // End of name, type A, class IN.
        return [.. bytes];
    }

    /// <summary>True for a reply to this query with no error code.</summary>
    public static bool IsAnswerTo(ushort id, byte[] reply) =>
        reply.Length >= HeaderSize
        && reply[0] == (byte)(id >> 8)
        && reply[1] == (byte)id
        && (reply[2] & 0x80) != 0
        && (reply[3] & 0x0F) == 0;
}

/// <summary>Ping, TCP ping and route tests. They only run when someone asks and need no admin rights.</summary>
public sealed class NetworkTools(INetworkProbe probe, Action<int>? wait = null)
{
    private const int Samples = 10;
    private const int Rounds = 5;
    private const int MaxHops = 30;
    private const int SilentHopLimit = 5;
    private const int TimeoutMs = 1000;
    private const int ConnectTimeoutMs = 2000;
    private const int SpacingMs = 400;

    private readonly Action<int> _wait = wait ?? Thread.Sleep;

    public ProbeSummary Ping(ProbeRequest request)
    {
        var address = Target(request.Host);
        var replies = Repeat(() =>
        {
            var reply = probe.Ping(address, 128, TimeoutMs);
            return reply.Reached ? reply.Ms : null;
        });
        return Summarize(request.Host, address, replies);
    }

    public ProbeSummary TcpPing(ProbeRequest request)
    {
        if (request.Port is < 1 or > 65535)
        {
            throw new CatalogRequestException("The port must be between 1 and 65535.");
        }

        var address = Target(request.Host);
        return Summarize(request.Host, address, Repeat(() => probe.Connect(address, request.Port, ConnectTimeoutMs)));
    }

    public TraceResult Trace(ProbeRequest request)
    {
        var address = Target(request.Host);
        var first = new List<PingReply>();
        var silent = 0;
        for (var ttl = 1; ttl <= MaxHops && silent < SilentHopLimit; ttl++)
        {
            var reply = probe.Ping(address, ttl, TimeoutMs);
            first.Add(reply);
            silent = reply.Address is null ? silent + 1 : 0;
            if (reply.Reached)
            {
                break;
            }
        }

        // Hops that never answered after the last one that did are only noise.
        var reached = first[^1].Reached;
        var count = reached ? first.Count : first.FindLastIndex(reply => reply.Address is not null) + 1;
        var hops = new TraceHop[count];
        Parallel.For(0, count, index =>
        {
            var replies = new List<PingReply> { first[index] };
            for (var round = 1; round < Rounds; round++)
            {
                replies.Add(probe.Ping(address, index + 1, TimeoutMs));
            }

            hops[index] = Hop(index + 1, replies);
        });

        return new TraceResult(request.Host, address.ToString(), reached, hops);
    }

    public IReadOnlyList<DnsBenchmarkRow> BenchmarkDns(IEnumerable<DnsAdapter> adapters)
    {
        var current = adapters
            .SelectMany(adapter => adapter.Servers)
            .FirstOrDefault(server => IPAddress.TryParse(server, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork);
        var targets = DnsPresets.All.Select(preset => (preset.Id, preset.Title, preset.Primary)).ToList();
        if (current is not null && targets.All(target => target.Primary != current))
        {
            targets.Insert(0, ("current", "Your current DNS", current));
        }

        var rows = new DnsBenchmarkRow[targets.Count];
        Parallel.For(0, targets.Count, index =>
        {
            var server = IPAddress.Parse(targets[index].Primary);
            var times = DnsPresets.TestDomains.Select(domain => probe.Lookup(server, domain, TimeoutMs)).ToList();
            var good = times.Where(time => time is not null).Select(time => time!.Value).Order().ToList();
            rows[index] = new DnsBenchmarkRow(
                targets[index].Id,
                targets[index].Title,
                targets[index].Primary,
                good.Count == 0 ? null : good[good.Count / 2],
                times.Count - good.Count);
        });

        return rows.OrderBy(row => row.MedianMs ?? double.MaxValue).ToList();
    }

    public static bool ValidHost(string host) =>
        host.Length is > 0 and <= 253
        && host[0] != '-'
        && host.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or ':');

    private IPAddress Target(string host)
    {
        var trimmed = host.Trim();
        return ValidHost(trimmed)
            ? probe.Resolve(trimmed)
            : throw new CatalogRequestException("Enter a website or an IP address.");
    }

    private List<double?> Repeat(Func<double?> sample)
    {
        var results = new List<double?>();
        for (var index = 0; index < Samples; index++)
        {
            results.Add(sample());
            if (index < Samples - 1)
            {
                _wait(SpacingMs);
            }
        }

        return results;
    }

    private static ProbeSummary Summarize(string host, IPAddress address, List<double?> replies)
    {
        var times = replies.Where(reply => reply is not null).Select(reply => reply!.Value).ToList();
        var jitter = times.Count < 2 ? (double?)null : times.Zip(times.Skip(1), (a, b) => Math.Abs(a - b)).Average();
        return new ProbeSummary(
            host,
            address.ToString(),
            replies.Count,
            times.Count,
            times.Count == 0 ? null : times.Min(),
            times.Count == 0 ? null : times.Average(),
            times.Count == 0 ? null : times.Max(),
            jitter,
            100d * (replies.Count - times.Count) / replies.Count);
    }

    private static TraceHop Hop(int number, List<PingReply> replies)
    {
        var answered = replies.Where(reply => reply.Address is not null).ToList();
        return new TraceHop(
            number,
            answered.FirstOrDefault().Address,
            replies.Count,
            answered.Count,
            answered.Count == 0 ? null : answered.Average(reply => reply.Ms),
            answered.Count == 0 ? null : answered.Min(reply => reply.Ms),
            answered.Count == 0 ? null : answered.Max(reply => reply.Ms),
            100d * (replies.Count - answered.Count) / replies.Count);
    }
}

public static class DnsPresets
{
    public static IReadOnlyList<string> TestDomains { get; } = ["example.com", "microsoft.com", "wikipedia.org", "cloudflare.com", "github.com"];

    public static IReadOnlyList<DnsPreset> All { get; } =
    [
        new("cloudflare", "Cloudflare", "Fast and private.", "1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001"),
        new("cloudflare-malware", "Cloudflare, malware blocking", "Blocks known malware sites.", "1.1.1.2", "1.0.0.2", "2606:4700:4700::1112", "2606:4700:4700::1002"),
        new("cloudflare-family", "Cloudflare, family", "Blocks malware and adult sites.", "1.1.1.3", "1.0.0.3", "2606:4700:4700::1113", "2606:4700:4700::1003"),
        new("google", "Google", "Fast and widely used.", "8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844"),
        new("quad9", "Quad9", "Blocks known malware sites.", "9.9.9.9", "149.112.112.112", "2620:fe::fe", "2620:fe::9"),
        new("opendns", "OpenDNS", "Long-running public DNS.", "208.67.222.222", "208.67.220.220", "2620:119:35::35", "2620:119:53::53"),
        new("adguard", "AdGuard", "Blocks ads and trackers.", "94.140.14.14", "94.140.15.15", "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff"),
        new("adguard-family", "AdGuard, family", "Blocks ads, trackers, malware and adult sites.", "94.140.14.15", "94.140.15.16", "2a10:50c0::bad1:ff", "2a10:50c0::bad2:ff"),
    ];
}

public sealed record DnsSavedAdapter(string Name, string[] Ipv4, string[] Ipv6);

public interface IDnsAdapters
{
    /// <summary>Connected adapters, with the DNS set on them by hand. Empty means automatic.</summary>
    IReadOnlyList<DnsSavedAdapter> ReadConfigured();

    /// <summary>The DNS servers in use right now, however they were set.</summary>
    IReadOnlyList<DnsAdapter> ReadCurrent();
}

public interface IDnsBackup
{
    bool Exists { get; }

    IReadOnlyList<DnsSavedAdapter>? Load();

    void Save(IReadOnlyList<DnsSavedAdapter> adapters);

    void Delete();
}

public sealed class WindowsDnsAdapters : IDnsAdapters
{
    private const string Ipv4Key = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";
    private const string Ipv6Key = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\";

    public IReadOnlyList<DnsSavedAdapter> ReadConfigured() => ActiveAdapters.List()
        .Select(adapter => new DnsSavedAdapter(adapter.Name, Saved(Ipv4Key, adapter.Id), Saved(Ipv6Key, adapter.Id)))
        .ToList();

    public IReadOnlyList<DnsAdapter> ReadCurrent() => ActiveAdapters.List()
        .Select(adapter => new DnsAdapter(
            adapter.Name,
            adapter.GetIPProperties().DnsAddresses.Select(address => address.ToString()).ToList()))
        .ToList();

    private static string[] Saved(string key, string id)
    {
        using var interfaceKey = Registry.LocalMachine.OpenSubKey(key + id);
        return (interfaceKey?.GetValue("NameServer") as string ?? string.Empty)
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
    }
}

/// <summary>Kept beside the journal, so only admins can change what a restore would apply.</summary>
public sealed class FileDnsBackup(string path) : IDnsBackup
{
    public bool Exists => File.Exists(path);

    public IReadOnlyList<DnsSavedAdapter>? Load()
    {
        try
        {
            return JsonSerializer.Deserialize<List<DnsSavedAdapter>>(File.ReadAllText(path), RpcProtocol.Json);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    public void Save(IReadOnlyList<DnsSavedAdapter> adapters) =>
        File.WriteAllText(path, JsonSerializer.Serialize(adapters, RpcProtocol.Json));

    public void Delete() => File.Delete(path);
}

/// <summary>Changes DNS on connected adapters, and keeps what was there before the first change.</summary>
public sealed class DnsService(IProcessRunner runner, IDnsAdapters adapters, IDnsBackup backup)
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(1);

    public OperationResult Set(DnsSetRequest request)
    {
        var plan = request.Mode switch
        {
            "preset" => Preset(request.PresetId),
            "custom" => Custom(request.Primary, request.Secondary),
            "automatic" => Everywhere([], []),
            "previous" => backup.Load() ?? throw new CatalogRequestException("There's no earlier DNS setting to restore."),
            _ => throw new CatalogRequestException("Unknown DNS option."),
        };

        if (plan.Count == 0)
        {
            return new OperationResult(false, "No connected network adapter was found.");
        }

        if (request.Mode == "previous")
        {
            var restored = Apply(plan);
            if (restored)
            {
                backup.Delete();
            }

            return Finish(restored, "Your earlier DNS is back.");
        }

        if (!backup.Exists)
        {
            backup.Save(adapters.ReadConfigured());
        }

        return Finish(Apply(plan), request.Mode == "automatic" ? "DNS is back to automatic." : "DNS was changed.");
    }

    private List<DnsSavedAdapter> Preset(string? id)
    {
        var preset = DnsPresets.All.FirstOrDefault(item => item.Id == id)
            ?? throw new CatalogRequestException("Unknown DNS provider.");
        return Everywhere([preset.Primary, preset.Secondary], [preset.Primary6, preset.Secondary6]);
    }

    private List<DnsSavedAdapter> Custom(string? primary, string? secondary)
    {
        var servers = new[] { primary, secondary }.Where(server => !string.IsNullOrWhiteSpace(server)).Select(server => server!.Trim()).ToArray();
        if (servers.Length == 0)
        {
            throw new CatalogRequestException("Enter a DNS server address.");
        }

        var parsed = servers.Select(server => IPAddress.TryParse(server, out var address) ? address : null).ToArray();
        if (parsed.Any(address => address is null))
        {
            throw new CatalogRequestException("Enter DNS servers as IP addresses.");
        }

        return Everywhere(
            servers.Where((_, index) => parsed[index]!.AddressFamily == AddressFamily.InterNetwork).ToArray(),
            servers.Where((_, index) => parsed[index]!.AddressFamily == AddressFamily.InterNetworkV6).ToArray());
    }

    private List<DnsSavedAdapter> Everywhere(string[] ipv4, string[] ipv6) =>
        adapters.ReadConfigured().Select(adapter => new DnsSavedAdapter(adapter.Name, ipv4, ipv6)).ToList();

    private bool Apply(IEnumerable<DnsSavedAdapter> plan)
    {
        var ok = true;
        foreach (var adapter in plan)
        {
            ok &= ApplyFamily("ipv4", adapter.Name, adapter.Ipv4);

            // Many PCs have IPv6 turned off, which is no failure unless only IPv6 was asked for.
            var ipv6 = ApplyFamily("ipv6", adapter.Name, adapter.Ipv6);
            ok &= ipv6 || adapter.Ipv4.Length > 0 || adapter.Ipv6.Length == 0;
        }

        return ok;
    }

    private bool ApplyFamily(string family, string adapter, string[] servers)
    {
        var name = $"name={adapter}";
        if (servers.Length == 0)
        {
            return Run(["interface", family, "set", "dnsservers", name, "source=dhcp"]);
        }

        var ok = Run(["interface", family, "set", "dnsservers", name, "source=static", $"address={servers[0]}", "register=primary", "validate=no"]);
        for (var index = 1; index < servers.Length; index++)
        {
            ok &= Run(["interface", family, "add", "dnsservers", name, $"address={servers[index]}", $"index={index + 1}", "validate=no"]);
        }

        return ok;
    }

    private bool Run(string[] arguments) => runner.Run("netsh.exe", arguments, Limit) == 0;

    private OperationResult Finish(bool ok, string message)
    {
        runner.Run("ipconfig.exe", ["/flushdns"], Limit);
        return ok
            ? new OperationResult(true, message)
            : new OperationResult(false, "Part of the DNS change failed. Check your connection, then try again.");
    }
}
