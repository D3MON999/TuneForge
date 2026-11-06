using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

/// <summary>Hardware and Windows details, read without admin rights.</summary>
public static class SystemInfoReader
{
    public static SystemSummary Read()
    {
        var computer = First("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem");
        var windows = First("SELECT Caption, BuildNumber FROM Win32_OperatingSystem");
        var cpu = First("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");

        return new SystemSummary(
            Environment.MachineName,
            Text(windows, "Caption"),
            Text(windows, "BuildNumber"),
            $"{Text(computer, "Manufacturer")} {Text(computer, "Model")}".Trim(),
            Text(cpu, "Name"),
            Convert.ToInt32(cpu?["NumberOfCores"] ?? 0, CultureInfo.InvariantCulture),
            Convert.ToInt32(cpu?["NumberOfLogicalProcessors"] ?? 0, CultureInfo.InvariantCulture),
            Gpus(),
            Convert.ToInt64(computer?["TotalPhysicalMemory"] ?? 0, CultureInfo.InvariantCulture),
            Environment.TickCount64 / 1000);
    }

    private static List<string> Gpus()
    {
        using var search = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
        return search.Get().Cast<ManagementBaseObject>().Select(item => Text(item, "Name")).Where(name => name.Length > 0).ToList();
    }

    private static ManagementBaseObject? First(string query)
    {
        using var search = new ManagementObjectSearcher(query);
        return search.Get().Cast<ManagementBaseObject>().FirstOrDefault();
    }

    private static string Text(ManagementBaseObject? item, string name) => (item?[name] as string)?.Trim() ?? string.Empty;
}

public interface IStorageReader
{
    StorageReport Read();
}

/// <summary>Uses the health Windows itself reports. Detailed SMART values would need admin rights.</summary>
public sealed class WindowsStorageReader : IStorageReader
{
    public StorageReport Read() => new(Disks(), Volumes());

    private static List<DiskInfo> Disks()
    {
        try
        {
            using var search = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT FriendlyName, MediaType, BusType, HealthStatus, Size FROM MSFT_PhysicalDisk");
            return search.Get().Cast<ManagementBaseObject>().Select(disk => new DiskInfo(
                (disk["FriendlyName"] as string)?.Trim() ?? "Disk",
                KindOf(Convert.ToInt32(disk["MediaType"], CultureInfo.InvariantCulture), Convert.ToInt32(disk["BusType"], CultureInfo.InvariantCulture)),
                Convert.ToInt64(disk["Size"], CultureInfo.InvariantCulture),
                HealthOf(Convert.ToInt32(disk["HealthStatus"], CultureInfo.InvariantCulture)))).ToList();
        }
        catch (ManagementException)
        {
            return [];
        }
    }

    public static string KindOf(int media, int bus) => (media, bus) switch
    {
        (_, 17) => "NVMe SSD",
        (_, 7) => "USB drive",
        (4, _) => "SSD",
        (3, _) => "Hard drive",
        _ => "Drive",
    };

    public static string HealthOf(int status) => status switch
    {
        0 => "healthy",
        1 => "warning",
        2 => "unhealthy",
        _ => "unknown",
    };

    private static List<VolumeInfo> Volumes() => DriveInfo.GetDrives()
        .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
        .Select(drive => new VolumeInfo(drive.Name, drive.VolumeLabel, drive.TotalSize, drive.AvailableFreeSpace))
        .ToList();
}

public interface IEventSource
{
    /// <summary>Newest first. Level is "errors" or "warnings", which also includes errors.</summary>
    IReadOnlyList<WindowsEvent> Read(string log, string level, int days, int limit);
}

public sealed class WindowsEventSource : IEventSource
{
    private const int MessageLimit = 400;

    public IReadOnlyList<WindowsEvent> Read(string log, string level, int days, int limit)
    {
        var levels = level == "warnings" ? "Level=1 or Level=2 or Level=3" : "Level=1 or Level=2";
        var span = days * 86_400_000L;
        var query = new EventLogQuery(
            log,
            PathType.LogName,
            $"*[System[({levels}) and TimeCreated[timediff(@SystemTime) <= {span.ToString(CultureInfo.InvariantCulture)}]]]")
        {
            ReverseDirection = true,
        };

        var events = new List<WindowsEvent>();
        try
        {
            using var reader = new EventLogReader(query);
            while (events.Count < limit && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    events.Add(new WindowsEvent(
                        (record.TimeCreated ?? DateTime.MinValue).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                        log,
                        record.ProviderName ?? "Unknown",
                        record.Id,
                        LevelName(record.Level),
                        Message(record)));
                }
            }
        }
        catch (EventLogException)
        {
            return events;
        }
        catch (UnauthorizedAccessException)
        {
            return events;
        }

        return events;
    }

    private static string LevelName(byte? level) => level switch
    {
        1 => "critical",
        2 => "error",
        3 => "warning",
        _ => "info",
    };

    private static string Message(EventRecord record)
    {
        try
        {
            var text = record.FormatDescription()?.ReplaceLineEndings(" ").Trim() ?? string.Empty;
            return text.Length > MessageLimit ? text[..MessageLimit] + "..." : text;
        }
        catch (EventLogException)
        {
            return string.Empty;
        }
    }
}

/// <summary>Reads Windows' logs. Only the two general logs can be asked for.</summary>
public sealed class EventService(IEventSource source)
{
    public const int SummaryDays = 7;
    private const int Limit = 500;
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(10);
    private static readonly string[] Logs = ["System", "Application"];

    public IReadOnlyList<WindowsEvent> Read(EventLogRequest request)
    {
        if (!Logs.Contains(request.Log) || request.Level is not ("errors" or "warnings"))
        {
            throw new CatalogRequestException("Unknown log.");
        }

        return source.Read(request.Log, request.Level, 30, Limit);
    }

    public IReadOnlyList<WindowsEvent> RecentErrors() =>
        Logs.SelectMany(log => source.Read(log, "errors", SummaryDays, Limit)).ToList();

    public static IReadOnlyList<ErrorGroup> Group(IEnumerable<WindowsEvent> events) => events
        .GroupBy(item => (item.Source, item.EventId))
        .Select(group =>
        {
            var newest = group.MaxBy(item => item.TimeUtc)!;
            return new ErrorGroup(group.Key.Source, group.Key.EventId, group.Count(), newest.TimeUtc, newest.Message);
        })
        .OrderByDescending(group => group.Count)
        .ToList();

    /// <summary>
    /// An unexpected restart or a blue screen. "Critical" alone isn't used, because Windows also
    /// gives that level to a single USB device driver failing.
    /// </summary>
    public static bool IsCrash(WindowsEvent item) =>
        (item.Source == "Microsoft-Windows-Kernel-Power" && item.EventId == 41)
        || (item.Source == "Microsoft-Windows-WER-SystemErrorReporting" && item.EventId == 1001);

    /// <summary>One crash logs several events around the restart, so nearby events count once.</summary>
    public static int CountCrashes(IEnumerable<WindowsEvent> events)
    {
        var count = 0;
        var last = DateTimeOffset.MinValue;
        foreach (var time in events.Where(IsCrash).Select(item => DateTimeOffset.Parse(item.TimeUtc, CultureInfo.InvariantCulture)).Order())
        {
            if (time - last > CrashWindow)
            {
                count++;
            }

            last = time;
        }

        return count;
    }
}

public sealed record HealthInput(
    StorageReport Storage,
    string SystemDrive,
    int Crashes,
    int Errors,
    int StartupPrograms,
    double MemoryPercent,
    double UptimeDays,
    long JunkBytes);

/// <summary>Starts at 100 and subtracts for each problem, so every lost point has a visible reason.</summary>
public static class HealthScorer
{
    private const double Gigabyte = 1024d * 1024 * 1024;

    public static HealthReport Score(HealthInput input)
    {
        var results = new List<(HealthCheck Check, int Lost)>
        {
            Disks(input.Storage.Disks),
            FreeSpace(input.Storage.Volumes, input.SystemDrive),
            Crashes(input.Crashes),
            Errors(input.Errors),
            Startup(input.StartupPrograms),
            Memory(input.MemoryPercent),
            Uptime(input.UptimeDays),
            Junk(input.JunkBytes),
        };

        return new HealthReport(
            Math.Clamp(100 - results.Sum(result => result.Lost), 0, 100),
            results.Select(result => result.Check).ToList());
    }

    private static (HealthCheck, int) Result(string id, string title, string status, string detail, int lost) =>
        (new HealthCheck(id, title, status, detail), lost);

    private static (HealthCheck, int) Disks(IReadOnlyList<DiskInfo> disks)
    {
        const string title = "Disk health";
        if (disks.Count == 0)
        {
            return Result("disks", title, "unknown", "Windows didn't report any disk health.", 0);
        }

        var failing = disks.Where(disk => disk.Health == "unhealthy").ToList();
        if (failing.Count > 0)
        {
            return Result("disks", title, "bad", $"{failing[0].Name} is failing. Back up your files now.", 30);
        }

        var warning = disks.FirstOrDefault(disk => disk.Health == "warning");
        return warning is not null
            ? Result("disks", title, "warn", $"Windows reports a warning for {warning.Name}. Back up your files soon.", 15)
            : Result("disks", title, "good", "No disk reports a problem.", 0);
    }

    private static (HealthCheck, int) FreeSpace(IReadOnlyList<VolumeInfo> volumes, string systemDrive)
    {
        const string title = "Free space";
        var drive = volumes.FirstOrDefault(volume => volume.Name.StartsWith(systemDrive, StringComparison.OrdinalIgnoreCase));
        if (drive is null || drive.TotalBytes == 0)
        {
            return Result("space", title, "unknown", "The Windows drive couldn't be read.", 0);
        }

        var percent = 100d * drive.FreeBytes / drive.TotalBytes;
        var detail = $"{drive.Name} has {drive.FreeBytes / Gigabyte:0.#} GB free ({percent:0}%).";
        return percent switch
        {
            < 5 => Result("space", title, "bad", detail, 20),
            < 15 => Result("space", title, "warn", detail, 10),
            _ => Result("space", title, "good", detail, 0),
        };
    }

    private static (HealthCheck, int) Crashes(int count) => count switch
    {
        0 => Result("crashes", "Crashes", "good", "No crashes in the last 7 days.", 0),
        1 => Result("crashes", "Crashes", "warn", "1 crash or unexpected restart in the last 7 days.", 10),
        2 => Result("crashes", "Crashes", "warn", "2 crashes or unexpected restarts in the last 7 days.", 10),
        _ => Result("crashes", "Crashes", "bad", $"{count} crashes or unexpected restarts in the last 7 days.", 20),
    };

    private static (HealthCheck, int) Errors(int count) => count > 100
        ? Result("errors", "Windows errors", "warn", $"{count} errors logged in the last 7 days.", 5)
        : Result("errors", "Windows errors", "good", $"{count} errors logged in the last 7 days.", 0);

    private static (HealthCheck, int) Startup(int count) => count > 15
        ? Result("startup", "Startup programs", "warn", $"{count} programs start with Windows, which slows startup.", 5)
        : Result("startup", "Startup programs", "good", $"Startup programs: {count}.", 0);

    private static (HealthCheck, int) Memory(double percent) => percent > 90
        ? Result("memory", "Memory", "warn", $"{percent:0}% of memory is in use right now.", 5)
        : Result("memory", "Memory", "good", $"{percent:0}% of memory is in use.", 0);

    private static (HealthCheck, int) Uptime(double days) => days > 14
        ? Result("uptime", "Restart", "warn", $"This PC has run for {days:0} days. Restart to finish updates.", 5)
        : Result("uptime", "Restart", "good", days < 1 ? "Restarted today." : $"Last restart: {days:0} days ago.", 0);

    private static (HealthCheck, int) Junk(long bytes) => bytes / Gigabyte >= 5
        ? Result("junk", "Junk files", "warn", $"{bytes / Gigabyte:0.#} GB can be cleaned.", 5)
        : Result("junk", "Junk files", "good", $"{bytes / Gigabyte:0.##} GB can be cleaned.", 0);
}

/// <summary>Gathers what the score and report need. Reads only, and needs no admin rights.</summary>
public sealed class HealthService(
    IStorageReader storage,
    EventService events,
    StartupService startup,
    CleanupService cleanup,
    ILiveProbe live)
{
    public HealthReport Check()
    {
        var errors = events.RecentErrors();
        var (used, total) = live.ReadMemory();
        return HealthScorer.Score(new HealthInput(
            storage.Read(),
            Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
            EventService.CountCrashes(errors),
            errors.Count,
            startup.List().Count(entry => entry.Enabled),
            total == 0 ? 0 : 100d * used / total,
            Environment.TickCount64 / 86_400_000d,
            cleanup.Scan().Sum(category => category.Bytes)));
    }

    public string BuildReport() => ReportWriter.Build(
        DateTimeOffset.Now,
        SystemInfoReader.Read(),
        Check(),
        storage.Read(),
        EventService.Group(events.RecentErrors()));
}

/// <summary>Writes a plain text report. It stays on this PC unless the person sends it to someone.</summary>
public static class ReportWriter
{
    private const double Gigabyte = 1024d * 1024 * 1024;

    public static string Build(
        DateTimeOffset when,
        SystemSummary system,
        HealthReport health,
        StorageReport storage,
        IReadOnlyList<ErrorGroup> errors)
    {
        var text = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;
        text.AppendLine(culture, $"TuneForge PC report, {when:yyyy-MM-dd HH:mm}");
        text.AppendLine();
        text.AppendLine(culture, $"Health score: {health.Score} out of 100");
        foreach (var check in health.Checks)
        {
            text.AppendLine(culture, $"  [{check.Status}] {check.Title}: {check.Detail}");
        }

        text.AppendLine();
        text.AppendLine("This PC");
        text.AppendLine(culture, $"  Name: {system.Computer}");
        text.AppendLine(culture, $"  Windows: {system.Windows} (build {system.Build})");
        text.AppendLine(culture, $"  Motherboard: {system.Board}");
        text.AppendLine(culture, $"  Processor: {system.Cpu}, {system.Cores} cores, {system.Threads} threads");
        foreach (var gpu in system.Gpus)
        {
            text.AppendLine(culture, $"  Graphics: {gpu}");
        }

        text.AppendLine(culture, $"  Memory: {system.MemoryBytes / Gigabyte:0.#} GB");
        text.AppendLine();
        text.AppendLine("Storage");
        foreach (var disk in storage.Disks)
        {
            text.AppendLine(culture, $"  {disk.Name}, {disk.Kind}, {disk.SizeBytes / Gigabyte:0} GB: {disk.Health}");
        }

        foreach (var volume in storage.Volumes)
        {
            text.AppendLine(culture, $"  {volume.Name} {volume.FreeBytes / Gigabyte:0.#} GB free of {volume.TotalBytes / Gigabyte:0.#} GB");
        }

        text.AppendLine();
        text.AppendLine(culture, $"Windows errors, last {EventService.SummaryDays} days");
        if (errors.Count == 0)
        {
            text.AppendLine("  None");
        }

        foreach (var group in errors.Take(25))
        {
            text.AppendLine(culture, $"  {group.Count} x {group.Source} (event {group.EventId}), last {group.LastUtc}");
            if (group.Message.Length > 0)
            {
                text.AppendLine(culture, $"    {group.Message}");
            }
        }

        return text.ToString();
    }
}
