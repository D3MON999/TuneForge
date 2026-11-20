using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

/// <summary>Reads nearby Wi-Fi through Windows' own Wi-Fi service. It only listens, and never connects anywhere.</summary>
public static class WifiReader
{
    private const int AccessDenied = 5;
    private const int ServiceNotRunning = 1062;
    private const int EntrySize = 360;

    public static int ChannelOf(int megahertz) => megahertz switch
    {
        2484 => 14,
        >= 2412 and < 2484 => (megahertz - 2407) / 5,
        >= 5955 and <= 7115 => (megahertz - 5950) / 5,
        >= 5000 and < 5955 => (megahertz - 5000) / 5,
        _ => 0,
    };

    public static string BandOf(int megahertz) => megahertz switch
    {
        < 3000 => "2.4 GHz",
        >= 5925 => "6 GHz",
        _ => "5 GHz",
    };

    /// <summary>Signal strength as Windows shows it: -100 dBm is 0%, -50 dBm or better is 100%.</summary>
    public static int SignalPercent(int rssi) => Math.Clamp(2 * (rssi + 100), 0, 100);

    public static WifiScan Scan()
    {
        if (WlanOpenHandle(2, 0, out _, out var client) != 0)
        {
            return new WifiScan(false, null, []);
        }

        try
        {
            if (WlanEnumInterfaces(client, 0, out var list) != 0)
            {
                return new WifiScan(false, null, []);
            }

            var interfaces = new List<Guid>();
            try
            {
                var count = Marshal.ReadInt32(list);
                for (var index = 0; index < count; index++)
                {
                    // Each entry is a GUID, a 256-character name and a state: 532 bytes, after an 8-byte header.
                    interfaces.Add(Marshal.PtrToStructure<Guid>(list + 8 + (index * 532)));
                }
            }
            finally
            {
                WlanFreeMemory(list);
            }

            if (interfaces.Count == 0)
            {
                return new WifiScan(false, null, []);
            }

            var networks = new List<WifiNetwork>();
            foreach (var id in interfaces)
            {
                var guid = id;
                var connected = ConnectedBssid(client, guid);
                WlanScan(client, ref guid, 0, 0, 0);
                Thread.Sleep(3000);
                var result = WlanGetNetworkBssList(client, ref guid, 0, 3, false, 0, out var bss);
                if (result == AccessDenied)
                {
                    return new WifiScan(true, "Windows only shares nearby Wi-Fi with apps when location is on. Turn on Settings > Privacy > Location > Let desktop apps access your location.", []);
                }

                if (result != 0)
                {
                    continue;
                }

                try
                {
                    var items = Marshal.ReadInt32(bss + 4);
                    for (var index = 0; index < items; index++)
                    {
                        var entry = bss + 8 + (index * EntrySize);
                        var length = Math.Min(Marshal.ReadInt32(entry), 32);
                        var name = Encoding.UTF8.GetString(ReadBytes(entry + 4, length));
                        var bssid = ReadBytes(entry + 40, 6);
                        var megahertz = Marshal.ReadInt32(entry + 92) / 1000;
                        networks.Add(new WifiNetwork(
                            name.Length == 0 ? "(hidden network)" : name,
                            ChannelOf(megahertz),
                            BandOf(megahertz),
                            SignalPercent(Marshal.ReadInt32(entry + 56)),
                            connected is not null && bssid.SequenceEqual(connected)));
                    }
                }
                finally
                {
                    WlanFreeMemory(bss);
                }
            }

            return new WifiScan(true, null, networks.OrderByDescending(network => network.Connected).ThenByDescending(network => network.SignalPercent).ToList());
        }
        catch (DllNotFoundException)
        {
            return new WifiScan(false, null, []);
        }
        finally
        {
            WlanCloseHandle(client, 0);
        }
    }

    /// <summary>The access point you're connected to, or null when not connected.</summary>
    private static byte[]? ConnectedBssid(nint client, Guid id)
    {
        if (WlanQueryInterface(client, ref id, 7, 0, out _, out var data, out _) != 0)
        {
            return null;
        }

        try
        {
            // State 1 is connected. The access point's address sits after the profile name and network name.
            return Marshal.ReadInt32(data) == 1 ? ReadBytes(data + 560, 6) : null;
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    private static byte[] ReadBytes(nint at, int count)
    {
        var bytes = new byte[count];
        Marshal.Copy(at, bytes, 0, count);
        return bytes;
    }

    public static bool ServiceMissing(int code) => code == ServiceNotRunning;

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint version, nint reserved, out uint negotiated, out nint client);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(nint client, nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(nint client, nint reserved, out nint list);

    [DllImport("wlanapi.dll")]
    private static extern int WlanScan(nint client, ref Guid id, nint ssid, nint ie, nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanGetNetworkBssList(nint client, ref Guid id, nint ssid, int type, [MarshalAs(UnmanagedType.Bool)] bool secure, nint reserved, out nint list);

    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(nint client, ref Guid id, int opcode, nint reserved, out int size, out nint data, out int kind);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(nint memory);
}

/// <summary>The speed each network adapter agreed with the router or switch, which caps everything above it.</summary>
public static class LinkSpeed
{
    private static readonly string[] Virtual =
    [
        "virtual", "hyper-v", "vpn", "tap", "tunnel", "loopback", "wan miniport", "bluetooth", "vmware", "virtualbox", "exitlag", "wintun", "wireguard",
        "filter", "scheduler", "kernel debug",
    ];

    public static bool IsPhysical(string description) =>
        !Virtual.Any(word => description.Contains(word, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<LinkInfo> Read(WifiScan? wifi = null)
    {
        var connected = wifi?.Networks.FirstOrDefault(network => network.Connected);
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up
                && adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
                && IsPhysical(adapter.Description) && IsPhysical(adapter.Name))
            .Select(adapter =>
            {
                var isWifi = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                return new LinkInfo(
                    adapter.Name,
                    isWifi ? "Wi-Fi" : "Ethernet",
                    adapter.Speed,
                    isWifi ? connected?.Name : null,
                    isWifi ? connected?.SignalPercent : null,
                    isWifi ? connected?.Channel : null,
                    isWifi ? connected?.Band : null);
            })
            .ToList();
    }

    /// <summary>The router's address, for telling a home network problem from an internet one.</summary>
    public static IPAddress? Router() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && IsPhysical(adapter.Description))
        .SelectMany(adapter => adapter.GetIPProperties().GatewayAddresses)
        .Select(gateway => gateway.Address)
        .FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !address.Equals(IPAddress.Any));
}

public interface IPinger
{
    /// <summary>Round trip in milliseconds, or null when there was no answer in time.</summary>
    int? Ping(IPAddress address, int timeoutMs);
}

public sealed class WindowsPinger : IPinger
{
    public int? Ping(IPAddress address, int timeoutMs)
    {
        try
        {
            using var ping = new Ping();
            var reply = ping.Send(address, timeoutMs);
            return reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : null;
        }
        catch (PingException)
        {
            return null;
        }
    }
}

/// <summary>
/// Pings your router and the internet once a second and writes down every drop and spike, with the time.
/// A drop at the router is a home network problem; a drop only beyond it is the internet provider's.
/// </summary>
public sealed class ConnectionLogger(IPinger pinger, string logPath, Func<DateTimeOffset> now)
{
    public const int SpikeFloorMs = 150;
    private const int TimeoutMs = 1000;
    private const int KeepEvents = 200;
    private static readonly IPAddress Internet = IPAddress.Parse("1.1.1.1");

    private readonly Lock _gate = new();
    private readonly List<ConnectionLogEvent> _events = [];
    private readonly Dictionary<string, Watch> _watches = new();
    private CancellationTokenSource? _stop;
    private DateTimeOffset? _since;
    private long _pings;
    private IPAddress? _router;

    public string LogPath => logPath;

    public ConnectionLogStatus Status()
    {
        lock (_gate)
        {
            return new ConnectionLogStatus(
                _stop is not null,
                _since?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                _pings,
                _router?.ToString(),
                _events.AsEnumerable().Reverse().ToList(),
                logPath);
        }
    }

    public void Start(IPAddress? router)
    {
        lock (_gate)
        {
            if (_stop is not null)
            {
                return;
            }

            _stop = new CancellationTokenSource();
            _since = now();
            _pings = 0;
            _events.Clear();
            _watches.Clear();
            _router = router;
            var token = _stop.Token;
            _ = Task.Run(() => Loop(token), token);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stop?.Cancel();
            _stop = null;
        }
    }

    private async Task Loop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var router = _router;
                var targets = router is null ? new[] { ("internet", Internet) } : [("router", router), ("internet", Internet)];
                var results = await Task.WhenAll(targets.Select(target => Task.Run(() => (target.Item1, pinger.Ping(target.Item2, TimeoutMs)), token)));
                foreach (var (where, ms) in results)
                {
                    Record(where, ms);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    /// <summary>One answer, or none. Public so the rules can be tested without a network.</summary>
    public void Record(string where, int? ms)
    {
        lock (_gate)
        {
            _pings++;
            var watch = _watches.TryGetValue(where, out var found) ? found : _watches[where] = new Watch();
            var at = now();

            // The internet can't answer when the router doesn't, so that drop is only the router's.
            var routerDown = where == "internet" && _watches.TryGetValue("router", out var home) && home.DownSince is not null;

            if (ms is null)
            {
                watch.DownSince ??= at;
                watch.Shadowed |= routerDown;
                return;
            }

            if (watch.DownSince is { } down)
            {
                var seconds = (int)Math.Max(1, (at - down).TotalSeconds);
                var shadowed = watch.Shadowed;
                watch.DownSince = null;
                watch.Shadowed = false;
                if (!shadowed)
                {
                    Add(new ConnectionLogEvent(down.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), where, "drop", seconds, null));
                }
            }

            var typical = watch.Typical();
            if (typical is { } usual && ms.Value >= Math.Max(SpikeFloorMs, usual * 3))
            {
                Add(new ConnectionLogEvent(at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), where, "spike", 1, ms));
            }

            watch.Add(ms.Value);
        }
    }

    private void Add(ConnectionLogEvent item)
    {
        _events.Add(item);
        if (_events.Count > KeepEvents)
        {
            _events.RemoveAt(0);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            if (!File.Exists(logPath))
            {
                File.WriteAllText(logPath, "time,where,what,seconds,ms" + Environment.NewLine);
            }

            var local = DateTimeOffset.Parse(item.WhenUtc, CultureInfo.InvariantCulture).ToLocalTime();
            File.AppendAllText(logPath, string.Join(',', local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), item.Where, item.Kind, item.Seconds.ToString(CultureInfo.InvariantCulture), item.Ms?.ToString(CultureInfo.InvariantCulture) ?? string.Empty) + Environment.NewLine);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The list on screen still has it.
        }
    }

    private sealed class Watch
    {
        private readonly Queue<int> _recent = new();

        public DateTimeOffset? DownSince { get; set; }

        /// <summary>The router was down too, so this drop is already in the log as the router's.</summary>
        public bool Shadowed { get; set; }

        public void Add(int ms)
        {
            _recent.Enqueue(ms);
            if (_recent.Count > 60)
            {
                _recent.Dequeue();
            }
        }

        /// <summary>The middle of the last minute, once there's enough to judge by.</summary>
        public int? Typical()
        {
            if (_recent.Count < 10)
            {
                return null;
            }

            var sorted = _recent.Order().ToList();
            return sorted[sorted.Count / 2];
        }
    }
}

/// <summary>Reads the hosts file, which sends names to fixed addresses. Removing lines needs admin rights.</summary>
public sealed class HostsFile(string path, string backupFolder)
{
    public static string DefaultPath => Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    public static IReadOnlyList<HostsEntry> Parse(string text)
    {
        var entries = new List<HostsEntry>();
        string? note = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                note = null;
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                var comment = trimmed.TrimStart('#').Trim();
                if (comment.Length > 0 && !LooksLikeEntry(comment))
                {
                    note = comment;
                }

                continue;
            }

            var body = trimmed.Split('#', 2);
            var parts = body[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out var address))
            {
                continue;
            }

            var inline = body.Length > 1 ? body[1].Trim() : null;
            var blocks = IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
            entries.Add(new HostsEntry(line, parts[0], parts[1..], string.IsNullOrEmpty(inline) ? note : inline, blocks));
        }

        // Windows' own example lines name localhost, and nothing else.
        return entries.Where(entry => !entry.Names.All(name => name.Equals("localhost", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private static bool LooksLikeEntry(string comment)
    {
        var first = comment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null && IPAddress.TryParse(first, out _);
    }

    public HostsReport Read() => new(
        File.Exists(path) ? Parse(File.ReadAllText(path)) : [],
        LatestBackup() is not null);

    private string? LatestBackup() =>
        Directory.Exists(backupFolder) ? Directory.EnumerateFiles(backupFolder, "hosts-*.bak").Order(StringComparer.Ordinal).LastOrDefault() : null;

    /// <summary>Removes only lines that match exactly, after saving a copy that Undo can put back.</summary>
    public OperationResult Remove(IReadOnlyList<string> lines)
    {
        if (!File.Exists(path))
        {
            return new OperationResult(false, "There's no hosts file.");
        }

        var text = File.ReadAllText(path);
        var removable = new HashSet<string>(Parse(text).Select(entry => entry.Text), StringComparer.Ordinal);
        var wanted = new HashSet<string>(lines.Where(removable.Contains), StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            throw new CatalogRequestException("Those lines are no longer in the hosts file.");
        }

        Directory.CreateDirectory(backupFolder);
        File.Copy(path, Path.Combine(backupFolder, $"hosts-{DateTime.UtcNow:yyyyMMddHHmmss}.bak"), overwrite: true);
        var kept = text.Split('\n').Where(line => !wanted.Contains(line.TrimEnd('\r')));
        File.WriteAllText(path, string.Join('\n', kept));
        return new OperationResult(true, wanted.Count == 1 ? "The line was removed." : $"{wanted.Count} lines were removed.");
    }

    public OperationResult Restore()
    {
        var backup = LatestBackup();
        if (backup is null)
        {
            return new OperationResult(false, "There's no earlier copy to put back.");
        }

        File.Copy(backup, path, overwrite: true);
        File.Delete(backup);
        return new OperationResult(true, "The hosts file is back the way it was.");
    }
}
