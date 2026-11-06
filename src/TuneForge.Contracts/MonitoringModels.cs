namespace TuneForge.Contracts;

public sealed record SystemSummary(
    string Computer,
    string Windows,
    string Build,
    string Board,
    string Cpu,
    int Cores,
    int Threads,
    IReadOnlyList<string> Gpus,
    long MemoryBytes,
    long UptimeSeconds);

/// <summary>Null means this PC can't report that value.</summary>
public sealed record LiveStats(
    double CpuPercent,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    double? DiskPercent,
    long NetworkDownPerSecond,
    long NetworkUpPerSecond,
    double? GpuPercent,
    double? GpuTemperature);

public sealed record DiskInfo(string Name, string Kind, long SizeBytes, string Health);

public sealed record VolumeInfo(string Name, string Label, long TotalBytes, long FreeBytes);

public sealed record StorageReport(IReadOnlyList<DiskInfo> Disks, IReadOnlyList<VolumeInfo> Volumes);

public sealed record WindowsEvent(string TimeUtc, string Log, string Source, int EventId, string Level, string Message);

public sealed record ErrorGroup(string Source, int EventId, int Count, string LastUtc, string Message);

public sealed record EventLogRequest(string Log, string Level);

public sealed record HealthCheck(string Id, string Title, string Status, string Detail);

public sealed record HealthReport(int Score, IReadOnlyList<HealthCheck> Checks);

public sealed record ReportResult(bool Saved, string? Path);

public sealed record ProbeRequest(string Host, int Port = 0);

public sealed record ProbeSummary(
    string Host,
    string Address,
    int Sent,
    int Received,
    double? MinMs,
    double? AvgMs,
    double? MaxMs,
    double? JitterMs,
    double LossPercent);

public sealed record TraceHop(int Hop, string? Address, int Sent, int Received, double? AvgMs, double? BestMs, double? WorstMs, double LossPercent);

public sealed record TraceResult(string Host, string Address, bool Reached, IReadOnlyList<TraceHop> Hops);

public sealed record DnsPreset(string Id, string Title, string Note, string Primary, string Secondary, string Primary6, string Secondary6);

public sealed record DnsAdapter(string Name, IReadOnlyList<string> Servers);

public sealed record DnsStatus(IReadOnlyList<DnsAdapter> Adapters, IReadOnlyList<DnsPreset> Presets, bool CanRestore);

public sealed record DnsBenchmarkRow(string Id, string Title, string Server, double? MedianMs, int Failed);

public sealed record DnsSetRequest(string Mode, string? PresetId = null, string? Primary = null, string? Secondary = null);
