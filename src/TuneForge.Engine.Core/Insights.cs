using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Xml.Linq;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

/// <summary>Finds what takes up space. It only reads, and never follows links out of the folder it's given.</summary>
public static class SpaceFinder
{
    private const int MaxItems = 40;
    private const int MaxLargest = 20;

    public static SpaceReport Scan(string path, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
        {
            throw new CatalogRequestException("That folder doesn't exist.");
        }

        var root = new DirectoryInfo(Path.GetFullPath(path));
        var here = new System.IO.EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var deep = new System.IO.EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = true };
        var largest = new PriorityQueue<SpaceEntry, long>();
        var items = new List<SpaceEntry>();

        void Consider(FileInfo file)
        {
            if (largest.Count < MaxLargest)
            {
                largest.Enqueue(new SpaceEntry(file.Name, file.FullName, file.Length, false), file.Length);
            }
            else if (largest.TryPeek(out _, out var smallest) && file.Length > smallest)
            {
                largest.EnqueueDequeue(new SpaceEntry(file.Name, file.FullName, file.Length, false), file.Length);
            }
        }

        foreach (var file in root.EnumerateFiles("*", here))
        {
            items.Add(new SpaceEntry(file.Name, file.FullName, file.Length, false));
            Consider(file);
        }

        foreach (var folder in root.EnumerateDirectories("*", here))
        {
            cancel.ThrowIfCancellationRequested();
            long bytes = 0;
            foreach (var file in folder.EnumerateFiles("*", deep))
            {
                bytes += file.Length;
                Consider(file);
            }

            items.Add(new SpaceEntry(folder.Name, folder.FullName, bytes, true));
        }

        var biggest = new List<SpaceEntry>();
        while (largest.TryDequeue(out var entry, out _))
        {
            biggest.Add(entry);
        }

        return new SpaceReport(
            root.FullName,
            items.Sum(item => item.Bytes),
            items.OrderByDescending(item => item.Bytes).Take(MaxItems).ToList(),
            biggest.OrderByDescending(item => item.Bytes).ToList());
    }
}

/// <summary>Reads the graphics driver from Windows. Whether a newer one exists is left to the maker's own page.</summary>
public static class GraphicsDrivers
{
    private static readonly Dictionary<string, string> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NVIDIA"] = "https://www.nvidia.com/Download/index.aspx",
        ["AMD"] = "https://www.amd.com/en/support/download/drivers.html",
        ["Intel"] = "https://www.intel.com/content/www/us/en/support/detect.html",
    };

    public static string? DownloadPage(string vendor) => Pages.GetValueOrDefault(vendor);

    public static string VendorOf(string pnpId)
    {
        var id = pnpId.ToUpperInvariant();
        return id.Contains("VEN_10DE") ? "NVIDIA" : id.Contains("VEN_1002") ? "AMD" : id.Contains("VEN_8086") ? "Intel" : "Other";
    }

    /// <summary>NVIDIA's own number is the last five digits of the Windows version, so 32.0.16.1714 is 617.14.</summary>
    public static string? Friendly(string vendor, string version)
    {
        if (!vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var digits = new string(version.Where(char.IsDigit).ToArray());
        return digits.Length < 5 ? null : $"{int.Parse(digits[^5..^2], CultureInfo.InvariantCulture)}.{digits[^2..]}";
    }

    public static GraphicsDriver Describe(string name, string pnpId, string version, DateTime? date, DateTime now)
    {
        var vendor = VendorOf(pnpId);
        return new GraphicsDriver(
            name,
            vendor,
            version,
            Friendly(vendor, version),
            date?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            date is null ? null : Math.Max(0, (int)(now - date.Value).TotalDays),
            DownloadPage(vendor) is not null);
    }

    /// <summary>Only real graphics cards. Virtual and basic display adapters have no driver worth updating.</summary>
    public static IReadOnlyList<GraphicsDriver> Read()
    {
        using var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController");
        var drivers = new List<GraphicsDriver>();
        foreach (var item in searcher.Get().Cast<ManagementObject>())
        {
            using (item)
            {
                var pnp = item["PNPDeviceID"] as string ?? string.Empty;
                if (!pnp.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) || VendorOf(pnp) == "Other")
                {
                    continue;
                }

                DateTime? date = item["DriverDate"] is string raw && raw.Length > 0 ? ManagementDateTimeConverter.ToDateTime(raw) : null;
                drivers.Add(Describe(item["Name"] as string ?? "Graphics card", pnp, item["DriverVersion"] as string ?? string.Empty, date, DateTime.Now));
            }
        }

        return drivers;
    }
}

/// <summary>Windows times each start itself. Reading those times needs admin rights, so this runs in the engine.</summary>
public static class BootHistory
{
    private const string Log = "Microsoft-Windows-Diagnostics-Performance/Operational";

    public static BootRecord? Parse(string eventXml, DateTime when)
    {
        var data = DataOf(eventXml);

        static int Number(Dictionary<string, string> values, string name) =>
            values.TryGetValue(name, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

        var total = Number(data, "BootTime");
        return total <= 0 ? null : new BootRecord(
            when.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            total,
            Number(data, "MainPathBootTime"),
            Number(data, "BootPostBootTime"));
    }

    private static Dictionary<string, string> DataOf(string eventXml) =>
        XDocument.Parse(eventXml).Descendants()
            .Where(element => element.Name.LocalName == "Data")
            .GroupBy(element => (string?)element.Attribute("Name") ?? string.Empty)
            .ToDictionary(group => group.Key, group => group.First().Value);

    /// <summary>One "this app took longer than usual to start" record: the app and how much it slowed the start.</summary>
    public static (string Name, string File, int AddedMs)? ParseSlowApp(string eventXml)
    {
        var data = DataOf(eventXml);
        var file = data.GetValueOrDefault("Name") is { Length: > 0 } name ? name : data.GetValueOrDefault("FileName") ?? string.Empty;
        if (file.Length == 0 || !int.TryParse(data.GetValueOrDefault("DegradationTime"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var added) || added <= 0)
        {
            return null;
        }

        var friendly = data.GetValueOrDefault("FriendlyName");
        return (string.IsNullOrWhiteSpace(friendly) ? file : friendly.Trim(), file.Trim(), added);
    }

    /// <summary>Worst first: the apps that slowed starts most often and by the most.</summary>
    public static IReadOnlyList<SlowStartupApp> Summarize(IEnumerable<(string Name, string File, int AddedMs)> records, int limit = 8) =>
        records
            .GroupBy(record => record.File, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SlowStartupApp(group.First().Name, group.First().File, group.Count(), (int)group.Average(record => record.AddedMs)))
            .OrderByDescending(app => (long)app.Times * app.AddedMs)
            .Take(limit)
            .ToList();

    /// <summary>Newest first. Empty if Windows isn't keeping these records.</summary>
    public static IReadOnlyList<BootRecord> Read(int limit = 20)
    {
        var boots = new List<BootRecord>();
        foreach (var (xml, when) in Records(100, limit))
        {
            if (Parse(xml, when) is { } boot)
            {
                boots.Add(boot);
            }
        }

        return boots;
    }

    /// <summary>Apps Windows saw slowing its start in the last 90 days.</summary>
    public static IReadOnlyList<SlowStartupApp> ReadSlowApps()
    {
        var since = DateTime.Now.AddDays(-90);
        return Summarize(Records(101, 300)
            .Where(record => record.When >= since)
            .Select(record => ParseSlowApp(record.Xml))
            .OfType<(string, string, int)>());
    }

    public static BootDiagnostics Diagnose() => new(Read(), ReadSlowApps());

    private static List<(string Xml, DateTime When)> Records(int eventId, int limit)
    {
        var found = new List<(string, DateTime)>();
        try
        {
            var query = new EventLogQuery(Log, PathType.LogName, $"*[System[EventID={eventId}]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            while (found.Count < limit && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    found.Add((record.ToXml(), record.TimeCreated ?? DateTime.MinValue));
                }
            }
        }
        catch (EventLogNotFoundException)
        {
            // This log is missing on some editions of Windows.
        }

        return found;
    }
}

/// <summary>Reads Windows' own battery report. It runs without admin rights and only reads.</summary>
public static class BatteryHealth
{
    public static BatteryReport Parse(string reportXml)
    {
        var batteries = XDocument.Parse(reportXml).Descendants()
            .Where(element => element.Name.LocalName == "Battery")
            .Select(battery =>
            {
                string? Text(string name) => battery.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } value ? value : null;
                long Number(string name) => long.TryParse(Text(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
                int? cycles = int.TryParse(Text("CycleCount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0 ? count : null;
                return new BatteryInfo(Text("Id"), Text("Manufacturer"), Number("DesignCapacity"), Number("FullChargeCapacity"), cycles);
            })
            .Where(battery => battery.DesignMwh > 0 || battery.FullMwh > 0)
            .ToList();
        return new BatteryReport(batteries);
    }

    public static BatteryReport Read(IProcessRunner runner)
    {
        var file = Path.Combine(Path.GetTempPath(), $"tuneforge-battery-{Guid.NewGuid():N}.xml");
        try
        {
            if (runner.Run("powercfg.exe", ["/batteryreport", "/xml", "/output", file, "/duration", "1"], TimeSpan.FromSeconds(30)) != 0 || !File.Exists(file))
            {
                // Desktops have no battery, and powercfg says so by failing.
                return new BatteryReport([]);
            }

            return Parse(File.ReadAllText(file));
        }
        finally
        {
            File.Delete(file);
        }
    }
}

/// <summary>Big files in Downloads that haven't been touched for months. It lists them; removing is up to the user.</summary>
public static class OldDownloads
{
    public const int MinAgeDays = 90;
    public const long MinBytes = 20L * 1024 * 1024;
    private const int MaxFiles = 100;

    public static OldDownloadsReport Scan(string folder, DateTime nowUtc)
    {
        if (!Directory.Exists(folder))
        {
            return new OldDownloadsReport(folder, []);
        }

        var options = new System.IO.EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System, RecurseSubdirectories = true };
        var files = new DirectoryInfo(folder).EnumerateFiles("*", options)
            .Where(file => file.Length >= MinBytes)
            .Select(file => (File: file, Used: file.LastWriteTimeUtc > file.CreationTimeUtc ? file.LastWriteTimeUtc : file.CreationTimeUtc))
            .Where(item => (nowUtc - item.Used).TotalDays >= MinAgeDays)
            .OrderByDescending(item => item.File.Length)
            .Take(MaxFiles)
            .Select(item => new OldDownload(
                item.File.Name,
                item.File.FullName,
                item.File.Length,
                item.Used.ToString("O", CultureInfo.InvariantCulture),
                (int)(nowUtc - item.Used).TotalDays))
            .ToList();
        return new OldDownloadsReport(folder, files);
    }

    /// <summary>Only files really inside the Downloads folder may be removed, whatever the page asks for.</summary>
    public static bool IsInside(string folder, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && full.Length > root.Length;
    }
}
