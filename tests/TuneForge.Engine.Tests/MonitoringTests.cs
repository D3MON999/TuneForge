using System.Net;
using TuneForge.Contracts;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

internal sealed class FakeProbe : ILiveProbe
{
    public CpuTimes Cpu { get; set; }

    public List<DiskTimes> Disks { get; set; } = [];

    public NetworkBytes Network { get; set; }

    public GpuReading? Gpu { get; set; }

    public CpuTimes ReadCpu() => Cpu;

    public (long Used, long Total) ReadMemory() => (4, 8);

    public IReadOnlyList<DiskTimes> ReadDisks() => Disks;

    public NetworkBytes ReadNetwork() => Network;

    public GpuReading? ReadGpu() => Gpu;
}

public class LiveMonitorTests
{
    [Fact]
    public void FirstSampleIsIdleThenUsesChangesSinceTheLastOne()
    {
        var now = 1000L;
        var probe = new FakeProbe
        {
            Cpu = new CpuTimes(100, 1000),
            Disks = [new DiskTimes(0, 100, 1000), new DiskTimes(1, 100, 1000)],
            Network = new NetworkBytes(1000, 5000),
            Gpu = new GpuReading(40, 60),
        };
        var monitor = new LiveMonitor(probe, () => now);

        var first = monitor.Sample();
        Assert.Equal(0, first.CpuPercent);
        Assert.Equal(0, first.NetworkDownPerSecond);

        now = 3000;
        probe.Cpu = new CpuTimes(125, 1100);
        probe.Disks = [new DiskTimes(0, 200, 1100), new DiskTimes(1, 150, 1100)];
        probe.Network = new NetworkBytes(3000, 6000);
        var second = monitor.Sample();

        Assert.Equal(75, second.CpuPercent, 3);
        Assert.Equal(50, second.DiskPercent!.Value, 3);
        Assert.Equal(1000, second.NetworkDownPerSecond);
        Assert.Equal(500, second.NetworkUpPerSecond);
        Assert.Equal(4, second.MemoryUsedBytes);
        Assert.Equal(40, second.GpuPercent);
        Assert.Equal(60, second.GpuTemperature);
    }

    [Fact]
    public void AdapterCounterRestartIsNotASpike()
    {
        var now = 1000L;
        var probe = new FakeProbe { Network = new NetworkBytes(9000, 9000) };
        var monitor = new LiveMonitor(probe, () => now);
        monitor.Sample();

        now = 3000;
        probe.Network = new NetworkBytes(10, 10);

        Assert.Equal(0, monitor.Sample().NetworkDownPerSecond);
    }

    [Fact]
    public void MissingValuesStayMissing()
    {
        var stats = new LiveMonitor(new FakeProbe()).Sample();

        Assert.Null(stats.DiskPercent);
        Assert.Null(stats.GpuPercent);
    }

    [Theory]
    [InlineData("8, 55\r\n", 8d, 55d)]
    [InlineData("100, [N/A]\n", 100d, null)]
    public void NvidiaOutputIsParsed(string text, double percent, double? temperature)
    {
        Assert.Equal(new GpuReading(percent, temperature), WindowsLiveProbe.ParseNvidia(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[N/A], [N/A]")]
    public void BadNvidiaOutputMeansNoReading(string text)
    {
        Assert.Null(WindowsLiveProbe.ParseNvidia(text));
    }
}

public class HealthScorerTests
{
    private static readonly StorageReport Fine = new(
        [new DiskInfo("SSD", "SSD", 100, "healthy")],
        [new VolumeInfo(@"C:\", "", 1000, 500)]);

    private static HealthInput Input(StorageReport? storage = null, int crashes = 0, int errors = 0, int startup = 3, double memory = 40, double uptime = 1, long junk = 0) =>
        new(storage ?? Fine, @"C:\", crashes, errors, startup, memory, uptime, junk);

    private static string Status(HealthReport report, string id) => report.Checks.Single(check => check.Id == id).Status;

    [Fact]
    public void AHealthyPcScores100()
    {
        var report = HealthScorer.Score(Input());

        Assert.Equal(100, report.Score);
        Assert.All(report.Checks, check => Assert.Equal("good", check.Status));
    }

    [Fact]
    public void AFailingDiskCostsTheMost()
    {
        var failing = new StorageReport([new DiskInfo("Old", "Hard drive", 1, "unhealthy")], Fine.Volumes);

        var report = HealthScorer.Score(Input(failing));

        Assert.Equal(70, report.Score);
        Assert.Equal("bad", Status(report, "disks"));
    }

    [Fact]
    public void UnknownDisksCostNothing()
    {
        var report = HealthScorer.Score(Input(new StorageReport([], Fine.Volumes)));

        Assert.Equal(100, report.Score);
        Assert.Equal("unknown", Status(report, "disks"));
    }

    [Theory]
    [InlineData(400, "good", 100)]
    [InlineData(100, "warn", 90)]
    [InlineData(40, "bad", 80)]
    public void FreeSpaceBands(long free, string status, int score)
    {
        var storage = new StorageReport(Fine.Disks, [new VolumeInfo(@"C:\", "", 1000, free)]);

        var report = HealthScorer.Score(Input(storage));

        Assert.Equal(status, Status(report, "space"));
        Assert.Equal(score, report.Score);
    }

    [Fact]
    public void EachSmallProblemCountsAndTheScoreNeverGoesBelowZero()
    {
        var report = HealthScorer.Score(Input(crashes: 1, errors: 500, startup: 30, memory: 95, uptime: 30, junk: 6L * 1024 * 1024 * 1024));
        Assert.Equal(65, report.Score);

        var worst = new StorageReport([new DiskInfo("Old", "Drive", 1, "unhealthy")], [new VolumeInfo(@"C:\", "", 1000, 1)]);
        Assert.True(HealthScorer.Score(Input(worst, crashes: 9, errors: 500, startup: 30, memory: 95, uptime: 30, junk: long.MaxValue)).Score >= 0);
    }
}

public class EventServiceTests
{
    private sealed class FakeEvents(params WindowsEvent[] events) : IEventSource
    {
        public List<string> Logs { get; } = [];

        public IReadOnlyList<WindowsEvent> Read(string log, string level, int days, int limit)
        {
            Logs.Add(log);
            return events;
        }
    }

    private static WindowsEvent Event(string source, int id, string time, string level = "error") =>
        new(time, "System", source, id, level, $"message {time}");

    [Fact]
    public void GroupsCountsAndKeepsTheNewestMessage()
    {
        var groups = EventService.Group(
        [
            Event("Disk", 7, "2026-11-01T00:00:00Z"),
            Event("Disk", 7, "2026-11-03T00:00:00Z"),
            Event("Other", 1, "2026-11-02T00:00:00Z"),
        ]);

        Assert.Equal(2, groups[0].Count);
        Assert.Equal("message 2026-11-03T00:00:00Z", groups[0].Message);
    }

    [Theory]
    [InlineData("Microsoft-Windows-Kernel-Power", 41, "error", true)]
    [InlineData("Microsoft-Windows-WER-SystemErrorReporting", 1001, "error", true)]
    [InlineData("Microsoft-Windows-DriverFrameworks-UserMode", 10116, "critical", false)]
    [InlineData("Disk", 7, "error", false)]
    public void RecognisesCrashes(string source, int id, string level, bool crash)
    {
        Assert.Equal(crash, EventService.IsCrash(Event(source, id, "t", level)));
    }

    [Fact]
    public void EventsFromOneCrashCountOnce()
    {
        const string power = "Microsoft-Windows-Kernel-Power";
        const string blueScreen = "Microsoft-Windows-WER-SystemErrorReporting";

        var count = EventService.CountCrashes(
        [
            Event(blueScreen, 1001, "2026-11-03T10:02:00Z"),
            Event(power, 41, "2026-11-03T10:00:00Z"),
            Event(power, 41, "2026-11-05T10:00:00Z"),
            Event("Disk", 7, "2026-11-05T11:00:00Z"),
        ]);

        Assert.Equal(2, count);
    }

    [Fact]
    public void OnlyTheGeneralLogsCanBeRead()
    {
        var source = new FakeEvents();
        var service = new EventService(source);

        service.Read(new EventLogRequest("System", "errors"));

        Assert.Throws<CatalogRequestException>(() => service.Read(new EventLogRequest("Security", "errors")));
        Assert.Throws<CatalogRequestException>(() => service.Read(new EventLogRequest("System", "everything")));
        Assert.Equal(["System"], source.Logs);
    }
}

public class ReportWriterTests
{
    [Fact]
    public void IncludesEverySection()
    {
        var text = ReportWriter.Build(
            new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero),
            new SystemSummary("PC", "Windows 11", "26300", "Board", "CPU", 8, 16, ["GPU"], 8L * 1024 * 1024 * 1024, 60),
            new HealthReport(90, [new HealthCheck("space", "Free space", "warn", "Low.")]),
            new StorageReport([new DiskInfo("SSD", "NVMe SSD", 1L << 40, "healthy")], [new VolumeInfo(@"C:\", "", 1L << 40, 1L << 39)]),
            [new ErrorGroup("Disk", 7, 3, "2026-11-05", "Bad block.")]);

        Assert.Contains("2026-11-06 12:00", text);
        Assert.Contains("Health score: 90 out of 100", text);
        Assert.Contains("[warn] Free space: Low.", text);
        Assert.Contains("CPU, 8 cores, 16 threads", text);
        Assert.Contains("Graphics: GPU", text);
        Assert.Contains("SSD, NVMe SSD, 1024 GB: healthy", text);
        Assert.Contains("3 x Disk (event 7)", text);
        Assert.Contains("Bad block.", text);
    }

    [Fact]
    public void SaysWhenThereAreNoErrors()
    {
        var text = ReportWriter.Build(
            DateTimeOffset.Now,
            new SystemSummary("PC", "W", "1", "B", "C", 1, 1, [], 0, 0),
            new HealthReport(100, []),
            new StorageReport([], []),
            []);

        Assert.Contains("None", text);
    }
}

public class NetworkToolsTests
{
    private sealed class FakeNetwork : INetworkProbe
    {
        public Queue<double?> Times { get; } = new();

        public Func<int, PingReply>? Hop { get; set; }

        public Dictionary<string, double?> Lookups { get; } = [];

        public IPAddress Resolve(string host) => IPAddress.Parse("10.0.0.1");

        public PingReply Ping(IPAddress address, int ttl, int timeoutMs)
        {
            if (Hop is not null)
            {
                return Hop(ttl);
            }

            return Times.Dequeue() is { } ms ? new PingReply(true, false, "10.0.0.1", ms) : new PingReply(false, false, null, 0);
        }

        public double? Connect(IPAddress address, int port, int timeoutMs) => Times.Dequeue();

        public double? Lookup(IPAddress server, string domain, int timeoutMs) => Lookups.GetValueOrDefault(server.ToString(), 20);
    }

    private static NetworkTools Tools(FakeNetwork network) => new(network, _ => { });

    private static void Fill(FakeNetwork network, params double?[] times)
    {
        foreach (var time in times)
        {
            network.Times.Enqueue(time);
        }
    }

    [Fact]
    public void Ping_ReportsLatencyJitterAndLoss()
    {
        var network = new FakeNetwork();
        Fill(network, 10, 20, null, 10, 20, 10, 20, 10, 20, null);

        var result = Tools(network).Ping(new ProbeRequest("example.com"));

        Assert.Equal(10, result.Sent);
        Assert.Equal(8, result.Received);
        Assert.Equal(20, result.LossPercent);
        Assert.Equal(10, result.MinMs);
        Assert.Equal(20, result.MaxMs);
        Assert.Equal(15, result.AvgMs);
        Assert.Equal(10, result.JitterMs);
    }

    [Fact]
    public void Ping_HandlesNoReplies()
    {
        var network = new FakeNetwork();
        Fill(network, null, null, null, null, null, null, null, null, null, null);

        var result = Tools(network).Ping(new ProbeRequest("example.com"));

        Assert.Equal(100, result.LossPercent);
        Assert.Null(result.AvgMs);
        Assert.Null(result.JitterMs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void TcpPing_RefusesBadPorts(int port)
    {
        Assert.Throws<CatalogRequestException>(() => Tools(new FakeNetwork()).TcpPing(new ProbeRequest("example.com", port)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-n 5 example.com")]
    [InlineData("a b")]
    [InlineData("example.com; del")]
    public void RefusesBadHosts(string host)
    {
        Assert.Throws<CatalogRequestException>(() => Tools(new FakeNetwork()).Ping(new ProbeRequest(host)));
    }

    [Fact]
    public void Trace_StopsAtTheDestinationAndCountsLossPerHop()
    {
        var network = new FakeNetwork
        {
            Hop = ttl => ttl switch
            {
                1 => new PingReply(false, true, "192.168.1.1", 1),
                2 => new PingReply(false, false, null, 0),
                3 => new PingReply(true, false, "10.0.0.1", 30),
                _ => throw new InvalidOperationException("Went past the destination."),
            },
        };

        var result = Tools(network).Trace(new ProbeRequest("example.com"));

        Assert.True(result.Reached);
        Assert.Equal(3, result.Hops.Count);
        Assert.Equal("192.168.1.1", result.Hops[0].Address);
        Assert.Equal(5, result.Hops[0].Received);
        Assert.Equal(100, result.Hops[1].LossPercent);
        Assert.Null(result.Hops[1].Address);
        Assert.Equal(30, result.Hops[2].AvgMs);
    }

    [Fact]
    public void Trace_GivesUpAfterSilentHopsAndDropsThem()
    {
        var network = new FakeNetwork
        {
            Hop = ttl => ttl == 1 ? new PingReply(false, true, "192.168.1.1", 1) : new PingReply(false, false, null, 0),
        };

        var result = Tools(network).Trace(new ProbeRequest("example.com"));

        Assert.False(result.Reached);
        Assert.Single(result.Hops);
    }

    [Fact]
    public void BenchmarkDns_SortsByTimeAndAddsTheCurrentServer()
    {
        var network = new FakeNetwork();
        network.Lookups["8.8.8.8"] = 5;
        network.Lookups["192.168.1.1"] = 2;
        network.Lookups["9.9.9.9"] = null;

        var rows = Tools(network).BenchmarkDns([new DnsAdapter("Ethernet", ["fe80::1", "192.168.1.1"])]);

        Assert.Equal("current", rows[0].Id);
        Assert.Equal("google", rows[1].Id);
        Assert.Equal("quad9", rows[^1].Id);
        Assert.Null(rows[^1].MedianMs);
        Assert.Equal(DnsPresets.TestDomains.Count, rows[^1].Failed);
    }

    [Fact]
    public void BenchmarkDns_DoesNotListAProviderTwice()
    {
        var rows = Tools(new FakeNetwork()).BenchmarkDns([new DnsAdapter("Ethernet", ["1.1.1.1"])]);

        Assert.DoesNotContain(rows, row => row.Id == "current");
        Assert.Equal(DnsPresets.All.Count, rows.Count);
    }
}

public class DnsPacketTests
{
    [Fact]
    public void BuildsAStandardQuestion()
    {
        var query = DnsPacket.BuildQuery(0x1234, "a.bc");

        Assert.Equal(
            new byte[] { 0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, (byte)'a', 2, (byte)'b', (byte)'c', 0, 0, 1, 0, 1 },
            query);
    }

    [Theory]
    [InlineData(new byte[] { 0x12, 0x34, 0x81, 0x80, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x12, 0x35, 0x81, 0x80, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x12, 0x34, 0x01, 0x80, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x12, 0x34, 0x81, 0x82, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x12, 0x34 }, false)]
    public void ChecksTheReply(byte[] reply, bool expected)
    {
        Assert.Equal(expected, DnsPacket.IsAnswerTo(0x1234, reply));
    }
}

public class DnsServiceTests
{
    private sealed class Calls : IProcessRunner
    {
        public List<string> Lines { get; } = [];

        public Func<IReadOnlyList<string>, int> Result { get; set; } = _ => 0;

        public int Run(string tool, IReadOnlyList<string> arguments, TimeSpan timeLimit)
        {
            Lines.Add($"{tool} {string.Join(' ', arguments)}");
            return Result(arguments);
        }
    }

    private sealed class Adapters(params DnsSavedAdapter[] adapters) : IDnsAdapters
    {
        public IReadOnlyList<DnsSavedAdapter> ReadConfigured() => adapters;

        public IReadOnlyList<DnsAdapter> ReadCurrent() => [];
    }

    private sealed class MemoryBackup : IDnsBackup
    {
        public IReadOnlyList<DnsSavedAdapter>? Saved { get; set; }

        public bool Exists => Saved is not null;

        public IReadOnlyList<DnsSavedAdapter>? Load() => Saved;

        public void Save(IReadOnlyList<DnsSavedAdapter> adapters) => Saved = adapters;

        public void Delete() => Saved = null;
    }

    private static readonly DnsSavedAdapter Automatic = new("Wi-Fi 2", [], []);

    [Fact]
    public void Preset_SetsBothServersAndKeepsTheOriginal()
    {
        var calls = new Calls();
        var backup = new MemoryBackup();

        var result = new DnsService(calls, new Adapters(Automatic), backup).Set(new DnsSetRequest("preset", "cloudflare"));

        Assert.True(result.Ok);
        Assert.Contains("netsh.exe interface ipv4 set dnsservers name=Wi-Fi 2 source=static address=1.1.1.1 register=primary validate=no", calls.Lines);
        Assert.Contains("netsh.exe interface ipv4 add dnsservers name=Wi-Fi 2 address=1.0.0.1 index=2 validate=no", calls.Lines);
        Assert.Contains(calls.Lines, line => line.Contains("ipv6 set dnsservers") && line.Contains("2606:4700:4700::1111"));
        Assert.Equal("ipconfig.exe /flushdns", calls.Lines[^1]);
        Assert.Equal([Automatic], backup.Saved);
    }

    [Fact]
    public void ASecondChangeKeepsTheFirstBackup()
    {
        var original = new DnsSavedAdapter("Ethernet", ["192.168.1.2"], []);
        var backup = new MemoryBackup { Saved = [original] };

        new DnsService(new Calls(), new Adapters(Automatic), backup).Set(new DnsSetRequest("preset", "google"));

        Assert.Equal([original], backup.Saved);
    }

    [Fact]
    public void Previous_RestoresAndClearsTheBackup()
    {
        var calls = new Calls();
        var backup = new MemoryBackup { Saved = [new DnsSavedAdapter("Ethernet", ["192.168.1.2"], [])] };

        var result = new DnsService(calls, new Adapters(Automatic), backup).Set(new DnsSetRequest("previous"));

        Assert.True(result.Ok);
        Assert.Contains(calls.Lines, line => line.Contains("name=Ethernet source=static address=192.168.1.2"));
        Assert.Contains("netsh.exe interface ipv6 set dnsservers name=Ethernet source=dhcp", calls.Lines);
        Assert.False(backup.Exists);
    }

    [Fact]
    public void Previous_KeepsTheBackupIfItFails()
    {
        var calls = new Calls { Result = _ => 1 };
        var backup = new MemoryBackup { Saved = [Automatic] };

        Assert.False(new DnsService(calls, new Adapters(Automatic), backup).Set(new DnsSetRequest("previous")).Ok);
        Assert.True(backup.Exists);
    }

    [Fact]
    public void Previous_NeedsABackup()
    {
        Assert.Throws<CatalogRequestException>(() =>
            new DnsService(new Calls(), new Adapters(Automatic), new MemoryBackup()).Set(new DnsSetRequest("previous")));
    }

    [Fact]
    public void Automatic_ClearsBothFamilies()
    {
        var calls = new Calls();

        new DnsService(calls, new Adapters(Automatic), new MemoryBackup()).Set(new DnsSetRequest("automatic"));

        Assert.Contains("netsh.exe interface ipv4 set dnsservers name=Wi-Fi 2 source=dhcp", calls.Lines);
        Assert.Contains("netsh.exe interface ipv6 set dnsservers name=Wi-Fi 2 source=dhcp", calls.Lines);
    }

    [Fact]
    public void Custom_SortsAddressesByFamily()
    {
        var calls = new Calls();

        var result = new DnsService(calls, new Adapters(Automatic), new MemoryBackup())
            .Set(new DnsSetRequest("custom", null, "4.4.4.4", "2001:db8::1"));

        Assert.True(result.Ok);
        Assert.Contains(calls.Lines, line => line.Contains("ipv4 set") && line.Contains("address=4.4.4.4"));
        Assert.Contains(calls.Lines, line => line.Contains("ipv6 set") && line.Contains("address=2001:db8::1"));
    }

    [Theory]
    [InlineData("mode", "cloudflare", "1.1.1.1")]
    [InlineData("preset", "not-real", null)]
    [InlineData("custom", null, "not an address")]
    [InlineData("custom", null, null)]
    public void RefusesBadRequests(string mode, string? preset, string? primary)
    {
        var calls = new Calls();

        Assert.Throws<CatalogRequestException>(() =>
            new DnsService(calls, new Adapters(Automatic), new MemoryBackup()).Set(new DnsSetRequest(mode, preset, primary)));
        Assert.Empty(calls.Lines);
    }

    [Fact]
    public void TurnedOffIpv6IsNotAFailure()
    {
        var calls = new Calls { Result = arguments => arguments.Contains("ipv6") ? 1 : 0 };

        Assert.True(new DnsService(calls, new Adapters(Automatic), new MemoryBackup()).Set(new DnsSetRequest("preset", "google")).Ok);
    }

    [Fact]
    public void Ipv4FailureIsReported()
    {
        var calls = new Calls { Result = arguments => arguments.Contains("ipv4") ? 1 : 0 };

        Assert.False(new DnsService(calls, new Adapters(Automatic), new MemoryBackup()).Set(new DnsSetRequest("preset", "google")).Ok);
    }

    [Fact]
    public void SaysSoWithNoAdapters()
    {
        var calls = new Calls();
        var backup = new MemoryBackup();

        Assert.False(new DnsService(calls, new Adapters(), backup).Set(new DnsSetRequest("preset", "google")).Ok);
        Assert.Empty(calls.Lines);
        Assert.False(backup.Exists);
    }
}
