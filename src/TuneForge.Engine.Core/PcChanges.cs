using System.Globalization;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

/// <summary>What was on the PC on one day, kept in your own folder so changes can be shown later.</summary>
public sealed record PcSnapshot(
    string Date,
    IReadOnlyList<string> Startup,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Programs,
    IReadOnlyList<string> Drivers);

/// <summary>Takes one snapshot a day and compares today with about a week ago. It only reads.</summary>
public sealed class SnapshotStore(string folder)
{
    private const int KeepDays = 45;
    private const int CompareDays = 7;

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TuneForge", "snapshots");

    public bool HasToday(DateOnly today) => File.Exists(FileFor(today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

    private string FileFor(string date) => Path.Combine(folder, date + ".json");

    public void Save(PcSnapshot snapshot, DateOnly today)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(FileFor(snapshot.Date), JsonSerializer.Serialize(snapshot, RpcProtocol.Json));
        foreach (var old in Dates().Where(date => date < today.AddDays(-KeepDays)))
        {
            File.Delete(FileFor(old.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }
    }

    private IEnumerable<DateOnly> Dates() =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json")
                .Select(file => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : (DateOnly?)null)
                .OfType<DateOnly>()
            : [];

    private PcSnapshot? Load(DateOnly date)
    {
        try
        {
            return JsonSerializer.Deserialize<PcSnapshot>(File.ReadAllText(FileFor(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))), RpcProtocol.Json);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Compares with the newest snapshot at least a week old, or the oldest one there is.</summary>
    public ChangesReport Compare(PcSnapshot current, DateOnly today)
    {
        var earlier = Dates().Where(date => date < today).Order().ToList();
        if (earlier.Count == 0)
        {
            return new ChangesReport(null, []);
        }

        var weekAgo = earlier.LastOrDefault(date => date <= today.AddDays(-CompareDays));
        var from = weekAgo == default ? earlier[0] : weekAgo;
        return Load(from) is { } before ? Diff(before, current) : new ChangesReport(null, []);
    }

    public static ChangesReport Diff(PcSnapshot before, PcSnapshot after)
    {
        static ChangeList List(string category, IReadOnlyList<string> was, IReadOnlyList<string> now) => new(
            category,
            now.Except(was, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            was.Except(now, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList());

        // A program that only changed version isn't new, so programs are matched without their numbers.
        static ChangeList Programs(IReadOnlyList<string> was, IReadOnlyList<string> now)
        {
            var before = was.Select(LeftoverFinder.Key).ToHashSet();
            var after = now.Select(LeftoverFinder.Key).ToHashSet();
            return new(
                "Programs",
                now.Where(name => !before.Contains(LeftoverFinder.Key(name))).Order(StringComparer.OrdinalIgnoreCase).ToList(),
                was.Where(name => !after.Contains(LeftoverFinder.Key(name))).Order(StringComparer.OrdinalIgnoreCase).ToList());
        }

        return new ChangesReport(before.Date,
        [
            Programs(before.Programs, after.Programs),
            List("Startup programs", before.Startup, after.Startup),
            List("Services", before.Services, after.Services),
            List("Drivers", before.Drivers, after.Drivers),
        ]);
    }

    public static PcSnapshot Take(StartupService startup, DateOnly today) => new(
        today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        startup.List().Where(entry => entry.Enabled).Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        ReadServices(),
        InstalledPrograms.Read(),
        DriverNames(DisplayDriverRemover.DefaultInfFolder));

    private static IReadOnlyList<string> ReadServices()
    {
        try
        {
            var services = ServiceController.GetServices();
            try
            {
                return services.Select(service => service.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            finally
            {
                foreach (var service in services)
                {
                    service.Dispose();
                }
            }
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Third-party driver packages, named by their maker and type, like "NVIDIA Display 32.0.15.8180".</summary>
    public static IReadOnlyList<string> DriverNames(string infFolder)
    {
        if (!Directory.Exists(infFolder))
        {
            return [];
        }

        var names = new List<string>();
        foreach (var file in Directory.EnumerateFiles(infFolder, "oem*.inf"))
        {
            try
            {
                names.Add(DriverName(File.ReadAllText(file)) ?? Path.GetFileName(file));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Skipped this time.
            }
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string? DriverName(string inf)
    {
        string? Value(string key) =>
            Regex.Match(inf, $@"^\s*{key}\s*=\s*(.+?)\s*(;.*)?$", RegexOptions.IgnoreCase | RegexOptions.Multiline) is { Success: true } match
                ? match.Groups[1].Value.Trim()
                : null;

        var provider = Value("Provider");
        if (provider is null)
        {
            return null;
        }

        if (provider.StartsWith('%') && provider.EndsWith('%'))
        {
            provider = Value(Regex.Escape(provider.Trim('%'))) ?? provider;
        }

        var kind = Value("Class") ?? "Driver";
        var version = Value("DriverVer")?.Split(',').ElementAtOrDefault(1)?.Trim();
        return $"{provider.Trim('"')} {kind}{(version is null ? string.Empty : " " + version)}";
    }
}

/// <summary>Programs listed in Settings > Apps, read the way Windows lists them.</summary>
public static class InstalledPrograms
{
    private static readonly (RegistryHive Hive, RegistryView View)[] Places =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Default),
    ];

    public static IReadOnlyList<string> Read()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view) in Places)
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null)
            {
                continue;
            }

            foreach (var id in uninstall.GetSubKeyNames())
            {
                using var item = uninstall.OpenSubKey(id);
                if (item?.GetValue("DisplayName") is string name && name.Length > 0
                    && item.GetValue("SystemComponent") is not 1
                    && item.GetValue("ParentKeyName") is null)
                {
                    names.Add(name.Trim());
                }
            }
        }

        return names.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Your health score, one per day, so the dashboard can show a trend.</summary>
public sealed class HealthHistory(string path)
{
    private const int KeepDays = 90;
    private readonly Lock _gate = new();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TuneForge", "health-history.json");

    public IReadOnlyList<HealthPoint> Read()
    {
        lock (_gate)
        {
            try
            {
                return JsonSerializer.Deserialize<List<HealthPoint>>(File.ReadAllText(path), RpcProtocol.Json) ?? [];
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    /// <summary>The day's latest score replaces an earlier one from the same day.</summary>
    public IReadOnlyList<HealthPoint> Record(int score, DateOnly today)
    {
        var date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var cutoff = today.AddDays(-KeepDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var points = Read().Where(point => point.Date != date && string.CompareOrdinal(point.Date, cutoff) > 0)
            .Append(new HealthPoint(date, score))
            .OrderBy(point => point.Date, StringComparer.Ordinal)
            .ToList();
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(points, RpcProtocol.Json));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The history is a nice extra, never a reason for a check to fail.
            }
        }

        return points;
    }
}

/// <summary>A readable name for a Store app's package id, like "Microsoft.WindowsStore_8wekyb3d8bbwe".</summary>
public static partial class AppNames
{
    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
    private static partial Regex WordBreak();

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-", RegexOptions.IgnoreCase)]
    private static partial Regex Guid();

    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase) { "Main", "App", "Desktop", "Core", "Client" };

    [GeneratedRegex(@"^(\d+|[0-9A-F]{6,}|[a-z]{2}-[A-Z]{2})$")]
    private static partial Regex Meaningless();

    /// <summary>Drops publisher numbers, versions and language codes, so "Microsoft.Ink.Handwriting.Main.en-US.1.0.1" reads "Handwriting Main".</summary>
    public static string FromPackage(string family)
    {
        var name = family.Split('_')[0];
        if (Guid().IsMatch(name))
        {
            return name;
        }

        var parts = name.Split('.').Where(part => part.Length > 0 && !Meaningless().IsMatch(part)).ToList();
        if (parts.Count == 0)
        {
            return name;
        }

        var last = parts[^1];
        var shown = Generic.Contains(last) && parts.Count > 1 ? $"{parts[^2]} {last}" : last;
        return WordBreak().Replace(shown, " ");
    }

    public static string FromPath(string marked)
    {
        var path = marked.Replace('#', '\\');
        return Path.GetFileNameWithoutExtension(path) is { Length: > 0 } file ? file : path;
    }

    public static bool IsPackageGuid(string family) => Guid().IsMatch(family);
}

/// <summary>Which apps used your camera, microphone and location, from Windows' own record. It only reads.</summary>
public static class PermissionReader
{
    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private static readonly (string Key, string Name)[] Capabilities = [("webcam", "camera"), ("microphone", "microphone"), ("location", "location")];

    public static IReadOnlyList<PermissionUse> Read()
    {
        var uses = new List<PermissionUse>();
        using var root = Registry.CurrentUser.OpenSubKey(Store);
        if (root is null)
        {
            return uses;
        }

        foreach (var (key, name) in Capabilities)
        {
            using var capability = root.OpenSubKey(key);
            if (capability is null)
            {
                continue;
            }

            foreach (var app in capability.GetSubKeyNames().Where(app => app != "NonPackaged"))
            {
                using var entry = capability.OpenSubKey(app);
                Add(uses, name, AppNames.FromPackage(app), entry);
            }

            using var desktop = capability.OpenSubKey("NonPackaged");
            foreach (var app in desktop?.GetSubKeyNames() ?? [])
            {
                using var entry = desktop!.OpenSubKey(app);
                Add(uses, name, AppNames.FromPath(app), entry);
            }
        }

        // The same app can appear once per version, so only its newest use is kept.
        return uses
            .GroupBy(use => (use.Capability, use.App.ToLowerInvariant()))
            .Select(group => group.OrderByDescending(use => use.InUse).ThenByDescending(use => use.LastUsedUtc, StringComparer.Ordinal).First())
            .OrderByDescending(use => use.InUse)
            .ThenByDescending(use => use.LastUsedUtc, StringComparer.Ordinal)
            .ToList();
    }

    private static void Add(List<PermissionUse> uses, string capability, string app, RegistryKey? entry)
    {
        if (entry?.GetValue("LastUsedTimeStart") is not long start || start <= 0)
        {
            return;
        }

        // An app that crashed mid-call never records a stop, so an old start with no stop isn't "now".
        var stop = entry.GetValue("LastUsedTimeStop") as long? ?? 0;
        var started = DateTime.FromFileTimeUtc(start);
        uses.Add(new PermissionUse(
            capability,
            app,
            started.ToString("O", CultureInfo.InvariantCulture),
            stop == 0 && DateTime.UtcNow - started < TimeSpan.FromHours(12)));
    }
}

/// <summary>
/// Store apps allowed to run in the background. Each switch is the same one as in Settings > Apps,
/// and it only affects your account.
/// </summary>
public static class BackgroundApps
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications";

    public static BackgroundAppsReport Read()
    {
        using var root = Registry.CurrentUser.OpenSubKey(Key);
        if (root is null)
        {
            return new BackgroundAppsReport(false, []);
        }

        var apps = root.GetSubKeyNames()
            .Where(id => id.Contains('_') && !AppNames.IsPackageGuid(id))
            .Select(id =>
            {
                using var app = root.OpenSubKey(id);
                return new BackgroundApp(id, AppNames.FromPackage(id), app?.GetValue("Disabled") is not 1);
            })
            .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new BackgroundAppsReport(root.GetValue("GlobalUserDisabled") is 1, apps);
    }

    public static BackgroundAppsReport Set(BackgroundAppRequest request)
    {
        using var root = Registry.CurrentUser.OpenSubKey(Key, writable: true);
        if (root is null || !root.GetSubKeyNames().Contains(request.Id, StringComparer.OrdinalIgnoreCase))
        {
            throw new CatalogRequestException("That app isn't installed for your account.");
        }

        using var app = root.OpenSubKey(request.Id, writable: true)!;
        if (request.Allowed)
        {
            app.DeleteValue("Disabled", throwOnMissingValue: false);
            app.DeleteValue("DisabledByUser", throwOnMissingValue: false);
        }
        else
        {
            app.SetValue("Disabled", 1, RegistryValueKind.DWord);
            app.SetValue("DisabledByUser", 1, RegistryValueKind.DWord);
        }

        return Read();
    }
}
