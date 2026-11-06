using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

public sealed record GogGame(string Title, string Folder, string Executable);

/// <summary>Where launchers keep their game lists. Tests point this at a temporary folder.</summary>
public sealed record GameSources(string? SteamFolder, string EpicManifests, IReadOnlyList<GogGame> GogGames)
{
    public static GameSources Detect()
    {
        using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return new GameSources(
            steam?.GetValue("SteamPath") as string,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic",
                "EpicGamesLauncher",
                "Data",
                "Manifests"),
            ReadGog());
    }

    private static List<GogGame> ReadGog()
    {
        using var games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        var found = new List<GogGame>();
        foreach (var name in games?.GetSubKeyNames() ?? [])
        {
            using var game = games!.OpenSubKey(name);
            if (game?.GetValue("gameName") is string title && game.GetValue("path") is string folder)
            {
                found.Add(new GogGame(title, folder, game.GetValue("exe") as string ?? string.Empty));
            }
        }

        return found;
    }
}

public sealed partial class GameScanner(GameSources sources)
{
    private const int SearchDepth = 4;
    private const int MaxProcessNames = 12;
    private const string SteamRedistributables = "228980";

    // Launchers call these out often enough that they would otherwise look like the game.
    private static readonly Regex NotAGame = HelperPattern();

    private static readonly (string Marker, string Name)[] AntiCheats =
    [
        ("EasyAntiCheat", "Easy Anti-Cheat"),
        ("EasyAntiCheat_EOS", "Easy Anti-Cheat"),
        ("BattlEye", "BattlEye"),
        ("BEService", "BattlEye"),
        ("vgc", "Riot Vanguard"),
        ("xigncode", "XIGNCODE3"),
        ("nProtect", "nProtect GameGuard"),
        ("PnkBstrA", "PunkBuster"),
    ];

    public IReadOnlyList<GameEntry> Scan(IReadOnlyList<GameProfile> profiles)
    {
        var known = profiles.ToDictionary(profile => profile.GameId);
        return Steam().Concat(Epic()).Concat(Gog())
            .Select(found => Describe(found.Id, found.Title, found.Launcher, found.Folder, found.Preferred, known))
            .OfType<GameEntry>()
            .OrderBy(game => game.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private IEnumerable<(string Id, string Title, string Launcher, string Folder, string? Preferred)> Steam()
    {
        if (string.IsNullOrEmpty(sources.SteamFolder))
        {
            yield break;
        }

        var libraries = new List<string> { sources.SteamFolder };
        var list = Path.Combine(sources.SteamFolder, "steamapps", "libraryfolders.vdf");
        if (File.Exists(list))
        {
            libraries.AddRange(LibraryPath().Matches(File.ReadAllText(list)).Select(match => Regex.Unescape(match.Groups[1].Value)));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries.Select(path => Path.GetFullPath(path)).Where(seen.Add))
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps))
            {
                continue;
            }

            foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(manifest);
                var id = Field(text, "appid");
                var title = Field(text, "name");
                var folder = Field(text, "installdir");
                if (id is not null && id != SteamRedistributables && title is not null && folder is not null)
                {
                    yield return ($"steam-{id}", title, "Steam", Path.Combine(apps, "common", folder), null);
                }
            }
        }
    }

    private IEnumerable<(string Id, string Title, string Launcher, string Folder, string? Preferred)> Epic()
    {
        if (!Directory.Exists(sources.EpicManifests))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(sources.EpicManifests, "*.item"))
        {
            EpicManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<EpicManifest>(File.ReadAllText(file));
            }
            catch (JsonException)
            {
                continue;
            }

            if (manifest is { DisplayName: not null, InstallLocation: not null })
            {
                yield return ($"epic-{Path.GetFileNameWithoutExtension(file)}", manifest.DisplayName, "Epic Games", manifest.InstallLocation, null);
            }
        }
    }

    private IEnumerable<(string Id, string Title, string Launcher, string Folder, string? Preferred)> Gog() =>
        sources.GogGames.Select(game => ($"gog-{game.Title}", game.Title, "GOG", game.Folder, (string?)game.Executable));

    private static GameEntry? Describe(
        string id,
        string title,
        string launcher,
        string folder,
        string? preferred,
        Dictionary<string, GameProfile> profiles)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var files = Files(folder, 0).ToList();
        var games = files
            .Where(file => file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !NotAGame.IsMatch(file.Name))
            .OrderByDescending(file => file.Length)
            .ToList();
        var main = games.FirstOrDefault(file => file.FullName.Equals(preferred, StringComparison.OrdinalIgnoreCase)) ?? games.FirstOrDefault();
        if (main is null)
        {
            return null;
        }

        var names = games.Take(MaxProcessNames).Select(file => Path.GetFileNameWithoutExtension(file.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new GameEntry(id, title, launcher, folder, main.FullName, names, FindAntiCheat(folder, files), profiles.GetValueOrDefault(id)?.Boosts ?? new GameBoosts());
    }

    private static string? FindAntiCheat(string folder, List<FileInfo> files)
    {
        var parts = files.Select(file => Path.GetRelativePath(folder, file.FullName))
            .SelectMany(path => path.Split(Path.DirectorySeparatorChar))
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return AntiCheats.FirstOrDefault(item => parts.Contains(item.Marker)).Name;
    }

    private static IEnumerable<FileInfo> Files(string folder, int depth)
    {
        DirectoryInfo directory = new(folder);
        FileSystemInfo[] entries;
        try
        {
            entries = directory.GetFileSystemInfos();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (entry is FileInfo file)
            {
                yield return file;
            }
            else if (depth < SearchDepth)
            {
                foreach (var inner in Files(entry.FullName, depth + 1))
                {
                    yield return inner;
                }
            }
        }
    }

    private static string? Field(string text, string name)
    {
        var match = Regex.Match(text, $"\"{name}\"\\s+\"(.*?)\"\\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Replace(@"\\", @"\") : null;
    }

    private sealed record EpicManifest(string? DisplayName, string? InstallLocation);

    [GeneratedRegex("\"path\"\\s+\"(.+?)\"")]
    private static partial Regex LibraryPath();

    [GeneratedRegex("(unins|setup|install|crash|report|redist|vc_?redist|dxsetup|dotnet|helper|launcher|bootstrap|service|update|easyanticheat|battleye|beservice|cleanup|prereq|touchup|ue4prereq)", RegexOptions.IgnoreCase)]
    private static partial Regex HelperPattern();
}

/// <summary>Adds or removes one setting inside a value Windows may also keep other settings in.</summary>
public static class GameFlagText
{
    public const string GpuHighPerformance = "GpuPreference=2;";
    public const string FullscreenOff = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

    public static string? WithGpu(string? value, bool on)
    {
        var parts = (value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase))
            .Select(part => part + ";")
            .ToList();
        if (on)
        {
            parts.Insert(0, GpuHighPerformance);
        }

        return parts.Count == 0 ? null : string.Concat(parts);
    }

    public static string? WithFullscreen(string? value, bool on)
    {
        var parts = (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part != "~" && part != FullscreenOff)
            .ToList();
        if (on)
        {
            parts.Add(FullscreenOff);
        }

        return parts.Count == 0 ? null : "~ " + string.Join(' ', parts);
    }
}

public interface IGameSettings
{
    string? ReadGpu(string executable);

    void WriteGpu(string executable, string? value);

    string? ReadFullscreen(string executable);

    void WriteFullscreen(string executable, string? value);

    int? ReadToasts();

    void WriteToasts(int? value);
}

/// <summary>Per-person Windows settings. None of these need admin rights.</summary>
public sealed class WindowsGameSettings : IGameSettings
{
    private const string GpuKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    private const string ToastKey = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";

    public string? ReadGpu(string executable) => Read(GpuKey, executable);

    public void WriteGpu(string executable, string? value) => Write(GpuKey, executable, value);

    public string? ReadFullscreen(string executable) => Read(LayersKey, executable);

    public void WriteFullscreen(string executable, string? value) => Write(LayersKey, executable, value);

    public int? ReadToasts()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ToastKey);
        return key?.GetValue("ToastEnabled") as int?;
    }

    public void WriteToasts(int? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ToastKey);
        if (value is { } number)
        {
            key.SetValue("ToastEnabled", number, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue("ToastEnabled", false);
        }
    }

    private static string? Read(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue(name) as string;
    }

    private static void Write(string path, string name, string? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path);
        if (value is null)
        {
            key.DeleteValue(name, false);
        }
        else
        {
            key.SetValue(name, value);
        }
    }
}

public interface IPowerPlans
{
    IReadOnlyList<PowerPlan> List();

    bool Activate(string id);

    bool AddUltimate();
}

public sealed partial class WindowsPowerPlans(IToolRunner runner) : IPowerPlans
{
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private static string Tool => Path.Combine(Environment.SystemDirectory, "powercfg.exe");

    public IReadOnlyList<PowerPlan> List() => Parse(runner.Capture(Tool, ["/list"], Limit).Text);

    public bool Activate(string id)
    {
        if (!Guid.TryParse(id, out _))
        {
            throw new CatalogRequestException("Unknown power plan.");
        }

        return runner.Capture(Tool, ["/setactive", id], Limit).ExitCode == 0;
    }

    public bool AddUltimate() => runner.Capture(Tool, ["/duplicatescheme", Ultimate], Limit).ExitCode == 0;

    /// <summary>The words are translated, so only the ID, the name in brackets and the star are read.</summary>
    public static IReadOnlyList<PowerPlan> Parse(string text) => text.Split('\n')
        .Select(line => PlanLine().Match(line.Trim()))
        .Where(match => match.Success)
        .Select(match => new PowerPlan(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Success))
        .ToList();

    [GeneratedRegex(@"([0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})\s+\((.*)\)\s*(\*)?$")]
    private static partial Regex PlanLine();
}

public static class ResizeBarCheck
{
    private const double SmallBarMib = 512;

    /// <summary>With Resize BAR off, the BAR is a small window of about 256 MB onto the card's memory.</summary>
    public static string Read(IToolRunner runner)
    {
        var tool = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        return File.Exists(tool)
            ? Parse(runner.Capture(tool, ["-q", "-d", "MEMORY"], TimeSpan.FromSeconds(5)).Text)
            : "unknown";
    }

    public static string Parse(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries);
        var heading = Array.FindIndex(lines, line => line.StartsWith("BAR1 Memory Usage", StringComparison.OrdinalIgnoreCase));
        if (heading < 0 || heading + 1 >= lines.Length)
        {
            return "unknown";
        }

        var number = lines[heading + 1].Split(':').LastOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var mib)
            ? mib > SmallBarMib ? "on" : "off"
            : "unknown";
    }
}

public interface IRunningApps
{
    IReadOnlySet<string> ProcessNames();

    /// <summary>Programs with a window, which are the only ones focus mode can close.</summary>
    IReadOnlyList<string> WindowedApps();

    void SetHighPriority(IEnumerable<string> names);

    void CloseApps(IEnumerable<string> names);
}

public sealed class WindowsRunningApps : IRunningApps
{
    private static readonly string[] NeverClose = ["explorer", "TuneForge", "dwm", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost", "TextInputHost"];

    public IReadOnlySet<string> ProcessNames() => Each(process => process.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> WindowedApps() => Each(process => process.MainWindowHandle != 0 ? process.ProcessName : null)
        .OfType<string>()
        .Except(NeverClose, StringComparer.OrdinalIgnoreCase)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public void SetHighPriority(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        process.PriorityClass = ProcessPriorityClass.High;
                    }
                    catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                    {
                        // A process that refuses simply keeps its normal priority.
                    }
                }
            }
        }
    }

    /// <summary>Asks politely, as the close button does, so apps can save work. Nothing is force-closed.</summary>
    public void CloseApps(IEnumerable<string> names)
    {
        foreach (var name in names.Except(NeverClose, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    process.CloseMainWindow();
                }
            }
        }
    }

    private static List<T> Each<T>(Func<Process, T> read)
    {
        var results = new List<T>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    results.Add(read(process));
                }
                catch (InvalidOperationException)
                {
                    // It exited while being read.
                }
            }
        }

        return results;
    }
}

/// <summary>
/// Applies a game's profile. Nothing here touches the game's memory or files. Process priority is
/// skipped for games with anti-cheat, because that's the one change aimed at the game's own process.
/// </summary>
public sealed class GameBooster(
    SettingsStore store,
    IGameSettings settings,
    IPowerPlans plans,
    IRunningApps apps,
    Action<string, string> notify)
{
    private const int ToastsOff = 0;

    public bool HasWork => store.Load() is { Profiles: { Count: > 0 } profiles } && profiles.Any(profile => profile.Boosts.AnyWhileRunning);

    public IReadOnlyList<GameProfile> Profiles => store.Load().Profiles ?? [];

    public void Save(GameEntry game, GameBoosts boosts)
    {
        settings.WriteGpu(game.Executable, GameFlagText.WithGpu(settings.ReadGpu(game.Executable), boosts.HighPerformanceGpu));
        settings.WriteFullscreen(game.Executable, GameFlagText.WithFullscreen(settings.ReadFullscreen(game.Executable), boosts.DisableFullscreenOptimizations));

        var keep = boosts != new GameBoosts();
        store.Update(current =>
        {
            var others = (current.Profiles ?? []).Where(profile => profile.GameId != game.Id);
            return current with
            {
                Profiles = keep
                    ? [.. others, new GameProfile(game.Id, game.Title, game.Executable, game.ProcessNames, game.AntiCheat, boosts)]
                    : others.ToList(),
            };
        });
    }

    public void Tick()
    {
        var current = store.Load();
        var running = apps.ProcessNames();
        if (current.Boost is { } boost)
        {
            var profile = current.Profiles?.FirstOrDefault(item => item.GameId == boost.GameId);
            if (profile is null || !profile.ProcessNames.Any(running.Contains))
            {
                Stop(boost);
            }

            return;
        }

        var game = current.Profiles?.FirstOrDefault(item => item.Boosts.AnyWhileRunning && item.ProcessNames.Any(running.Contains));
        if (game is not null)
        {
            Start(game, current);
        }
    }

    private void Start(GameProfile game, AppSettings current)
    {
        var boosts = game.Boosts;
        var active = plans.List().FirstOrDefault(plan => plan.Active)?.Id;
        var changePlan = boosts.PowerBoost && active is not null && !IsHighPerformance(active);
        var toasts = settings.ReadToasts();
        var changeToasts = boosts.Focus && toasts != ToastsOff;

        // Saved first, so even a crash right after this can be put back.
        store.Update(saved => saved with { Boost = new ActiveBoost(game.GameId, game.Title, active, changePlan, changeToasts, toasts) });

        if (changePlan)
        {
            plans.Activate(WindowsPowerPlans.HighPerformance);
        }

        if (changeToasts)
        {
            settings.WriteToasts(ToastsOff);
        }

        if (boosts.Focus)
        {
            apps.CloseApps(current.FocusApps ?? []);
        }

        if (boosts.HighPriority && game.AntiCheat is null)
        {
            apps.SetHighPriority(game.ProcessNames);
        }

        if (current.Choices.Notifications && !boosts.Focus)
        {
            notify("Game boost on", $"{game.Title} is boosted.");
        }
    }

    /// <summary>Also runs at startup, in case TuneForge or the PC stopped during a game.</summary>
    public void Recover()
    {
        var current = store.Load();
        if (current.Boost is { } boost)
        {
            var profile = current.Profiles?.FirstOrDefault(item => item.GameId == boost.GameId);
            var running = apps.ProcessNames();
            if (profile is null || !profile.ProcessNames.Any(running.Contains))
            {
                Stop(boost);
            }
        }
    }

    private void Stop(ActiveBoost boost)
    {
        if (boost.ChangedPlan && boost.PreviousPlan is not null)
        {
            plans.Activate(boost.PreviousPlan);
        }

        if (boost.ChangedToasts)
        {
            settings.WriteToasts(boost.PreviousToasts);
        }

        store.Update(saved => saved with { Boost = null });
    }

    private bool IsHighPerformance(string id) =>
        id.Equals(WindowsPowerPlans.HighPerformance, StringComparison.OrdinalIgnoreCase);
}
