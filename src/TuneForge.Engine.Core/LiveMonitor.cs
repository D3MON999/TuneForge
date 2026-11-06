using System.ComponentModel;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TuneForge.Contracts;

namespace TuneForge.Engine.Core;

public readonly record struct CpuTimes(long Idle, long Total);

public readonly record struct DiskTimes(int Drive, long Idle, long Time);

public readonly record struct NetworkBytes(long Received, long Sent);

public readonly record struct GpuReading(double Percent, double? Temperature);

public interface ILiveProbe
{
    CpuTimes ReadCpu();

    (long Used, long Total) ReadMemory();

    IReadOnlyList<DiskTimes> ReadDisks();

    NetworkBytes ReadNetwork();

    GpuReading? ReadGpu();
}

/// <summary>Turns running totals into usage since the last sample. The first sample reads as idle.</summary>
public sealed class LiveMonitor(ILiveProbe probe, Func<long>? clockMs = null)
{
    private readonly Lock _gate = new();
    private readonly Func<long> _clockMs = clockMs ?? (() => Environment.TickCount64);
    private CpuTimes? _cpu;
    private Dictionary<int, DiskTimes> _disks = [];
    private NetworkBytes? _network;
    private long _lastMs;

    public LiveStats Sample()
    {
        lock (_gate)
        {
            var now = _clockMs();
            var seconds = (now - _lastMs) / 1000d;
            var cpu = probe.ReadCpu();
            var (used, total) = probe.ReadMemory();
            var disks = probe.ReadDisks();
            var network = probe.ReadNetwork();
            var gpu = probe.ReadGpu();

            var stats = new LiveStats(
                CpuPercent(cpu),
                used,
                total,
                BusiestDisk(disks),
                _network is { } before && seconds > 0 ? Rate(network.Received - before.Received, seconds) : 0,
                _network is { } previous && seconds > 0 ? Rate(network.Sent - previous.Sent, seconds) : 0,
                gpu?.Percent,
                gpu?.Temperature);

            _cpu = cpu;
            _disks = disks.ToDictionary(disk => disk.Drive);
            _network = network;
            _lastMs = now;
            return stats;
        }
    }

    private double CpuPercent(CpuTimes now)
    {
        if (_cpu is not { } before || now.Total <= before.Total)
        {
            return 0;
        }

        var busy = 1d - ((double)(now.Idle - before.Idle) / (now.Total - before.Total));
        return Math.Clamp(busy * 100, 0, 100);
    }

    private double? BusiestDisk(IReadOnlyList<DiskTimes> disks)
    {
        if (disks.Count == 0)
        {
            return null;
        }

        var busiest = 0d;
        foreach (var disk in disks)
        {
            if (_disks.TryGetValue(disk.Drive, out var before) && disk.Time > before.Time)
            {
                var idle = (double)(disk.Idle - before.Idle) / (disk.Time - before.Time);
                busiest = Math.Max(busiest, Math.Clamp((1 - idle) * 100, 0, 100));
            }
        }

        return busiest;
    }

    // A counter that restarts after a network change must not show as a huge spike or a negative.
    private static long Rate(long bytes, double seconds) => bytes <= 0 ? 0 : (long)(bytes / seconds);
}

/// <summary>Reads Windows directly, so this works even when performance counters are turned off.</summary>
public sealed class WindowsLiveProbe(IToolRunner runner) : ILiveProbe
{
    private const uint DiskPerformanceControl = 0x70020;
    private const int MaxDrives = 16;
    private const uint ShareReadWrite = 3;
    private const uint OpenExisting = 3;
    private static readonly TimeSpan GpuLimit = TimeSpan.FromSeconds(3);

    private static readonly string NvidiaTool = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
    private readonly bool _hasNvidiaTool = File.Exists(NvidiaTool);

    public CpuTimes ReadCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            throw new Win32Exception();
        }

        // Kernel time already includes idle time.
        return new CpuTimes(idle, kernel + user);
    }

    public (long Used, long Total) ReadMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new Win32Exception();
        }

        return ((long)(status.TotalPhysical - status.AvailablePhysical), (long)status.TotalPhysical);
    }

    public IReadOnlyList<DiskTimes> ReadDisks()
    {
        var disks = new List<DiskTimes>();
        for (var drive = 0; drive < MaxDrives; drive++)
        {
            using var handle = CreateFile($@"\\.\PhysicalDrive{drive}", 0, ShareReadWrite, 0, OpenExisting, 0, 0);
            if (!handle.IsInvalid
                && DeviceIoControl(handle, DiskPerformanceControl, 0, 0, out var data, (uint)Marshal.SizeOf<DiskPerformance>(), out _, 0))
            {
                disks.Add(new DiskTimes(drive, data.IdleTime, data.QueryTime));
            }
        }

        return disks;
    }

    public NetworkBytes ReadNetwork()
    {
        long received = 0;
        long sent = 0;
        foreach (var adapter in ActiveAdapters.List())
        {
            var statistics = adapter.GetIPStatistics();
            received += statistics.BytesReceived;
            sent += statistics.BytesSent;
        }

        return new NetworkBytes(received, sent);
    }

    public GpuReading? ReadGpu()
    {
        if (!_hasNvidiaTool)
        {
            return null;
        }

        var output = runner.Capture(
            NvidiaTool,
            ["--query-gpu=utilization.gpu,temperature.gpu", "--format=csv,noheader,nounits"],
            GpuLimit);
        return output.ExitCode == 0 ? ParseNvidia(output.Text) : null;
    }

    /// <summary>The tool prints one line per GPU, like "8, 55". Unsupported values print as text.</summary>
    public static GpuReading? ParseNvidia(string text)
    {
        var parts = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Split(',', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 2 } || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return null;
        }

        return new GpuReading(
            percent,
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature) ? temperature : null);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, nint input, uint inputSize, out DiskPerformance output, uint outputSize, out uint returned, nint overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DiskPerformance
    {
        public long BytesRead;
        public long BytesWritten;
        public long ReadTime;
        public long WriteTime;
        public long IdleTime;
        public uint ReadCount;
        public uint WriteCount;
        public uint QueueDepth;
        public uint SplitCount;
        public long QueryTime;
        public uint DeviceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)]
        public string ManagerName;
    }
}

/// <summary>Real connections only: running adapters with a gateway, which skips virtual switches.</summary>
public static class ActiveAdapters
{
    public static IEnumerable<NetworkInterface> List() => NetworkInterface.GetAllNetworkInterfaces().Where(adapter =>
        adapter.OperationalStatus == OperationalStatus.Up
        && adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        && adapter.GetIPProperties().GatewayAddresses.Any(gateway => !gateway.Address.Equals(System.Net.IPAddress.Any)));
}
