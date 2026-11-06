using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

/// <summary>Power settings that go beyond switching plans. None of these need admin rights.</summary>
public sealed partial class PowerTools(IToolRunner runner)
{
    private const int MaxNameLength = 40;
    private const string MinimumCores = "CPMINCORES";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private static string Tool => Path.Combine(Environment.SystemDirectory, "powercfg.exe");

    /// <summary>Copies the active plan, so the new one starts from settings that already work.</summary>
    public OperationResult CreatePlan(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaxNameLength || !trimmed.All(character => char.IsLetterOrDigit(character) || character is ' ' or '-'))
        {
            throw new CatalogRequestException("Use letters, numbers and spaces, up to 40 characters.");
        }

        var output = runner.Capture(Tool, ["/duplicatescheme", "SCHEME_CURRENT"], Limit);
        var id = PlanId().Match(output.Text);
        if (output.ExitCode != 0 || !id.Success)
        {
            return new OperationResult(false, "Windows couldn't create the plan.");
        }

        return runner.Capture(Tool, ["/changename", id.Value, trimmed], Limit).ExitCode == 0
            ? new OperationResult(true, $"{trimmed} was created.")
            : new OperationResult(false, "The plan was created, but it couldn't be named.");
    }

    /// <summary>Windows hides this setting, so only the /qh form of the query returns it.</summary>
    public CoreParkingStatus ReadCoreParking() =>
        new(ParseMinimumCores(runner.Capture(Tool, ["/qh", "SCHEME_CURRENT", "SUB_PROCESSOR", MinimumCores], Limit).Text));

    /// <summary>Windows prints the plugged-in value then the battery one, as hex at the end of each line.</summary>
    public static int? ParseMinimumCores(string text)
    {
        var values = HexValue().Matches(text);
        return values.Count < 2
            ? null
            : int.Parse(values[^2].Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public OperationResult KeepCoresAwake()
    {
        var set = runner.Capture(Tool, ["/setacvalueindex", "SCHEME_CURRENT", "SUB_PROCESSOR", MinimumCores, "100"], Limit).ExitCode;
        var apply = runner.Capture(Tool, ["/setactive", "SCHEME_CURRENT"], Limit).ExitCode;
        return set == 0 && apply == 0
            ? new OperationResult(true, "All CPU cores now stay awake when plugged in.")
            : new OperationResult(false, "Windows couldn't change core parking on this plan.");
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}")]
    private static partial Regex PlanId();

    [GeneratedRegex(@":\s*0x([0-9a-fA-F]{8})\s*$", RegexOptions.Multiline)]
    private static partial Regex HexValue();
}

public interface IDisplayModes
{
    DisplayModeInfo? Read();

    bool SetBest();
}

/// <summary>Uses the best refresh rate the main display supports at its current resolution.</summary>
public sealed class WindowsDisplayModes : IDisplayModes
{
    private const int CurrentSettings = -1;
    private const uint FieldRefresh = 0x400000;

    public DisplayModeInfo? Read()
    {
        var current = Current();
        return current is null ? null : new DisplayModeInfo(
            $"{current.Value.Width} x {current.Value.Height}",
            (int)current.Value.Frequency,
            (int)Best(current.Value));
    }

    public bool SetBest()
    {
        if (Current() is not { } current)
        {
            return false;
        }

        var best = Best(current);
        if (best <= current.Frequency)
        {
            return true;
        }

        current.Frequency = best;
        current.Fields = FieldRefresh;
        return ChangeDisplaySettings(ref current, 0) == 0;
    }

    private static DevMode? Current()
    {
        var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
        return EnumDisplaySettings(null, CurrentSettings, ref mode) ? mode : null;
    }

    private static uint Best(DevMode current)
    {
        var best = current.Frequency;
        var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
        for (var index = 0; EnumDisplaySettings(null, index, ref mode); index++)
        {
            if (mode.Width == current.Width && mode.Height == current.Height && mode.BitsPerPixel == current.BitsPerPixel)
            {
                best = Math.Max(best, mode.Frequency);
            }
        }

        return best;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string? device, int mode, ref DevMode data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsW")]
    private static extern int ChangeDisplaySettings(ref DevMode data, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public short SpecVersion;
        public short DriverVersion;
        public short Size;
        public short DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public int Orientation;
        public int FixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public short LogPixels;
        public uint BitsPerPixel;
        public uint Width;
        public uint Height;
        public uint DisplayFlags;
        public uint Frequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }
}

public sealed record ConnectionEntry(string Protocol, string Local, string Remote, string State, int ProcessId, string Program, bool Listening);

/// <summary>Reads Windows' list of network connections, and remembers remote addresses seen this session.</summary>
public sealed class ConnectionScanner(IToolRunner runner)
{
    private const int HistoryLimit = 200;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _seen = [];

    public IReadOnlyList<ConnectionEntry> Scan(Func<int, string> programName)
    {
        var text = runner.Capture(Path.Combine(Environment.SystemDirectory, "netstat.exe"), ["-ano"], Limit).Text;
        var connections = Parse(text).Select(row => row with { Program = programName(row.ProcessId) }).ToList();
        lock (_gate)
        {
            foreach (var connection in connections.Where(item => !item.Listening && _seen.Count < HistoryLimit))
            {
                _seen.TryAdd($"{connection.Program} {connection.Remote}", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            }
        }

        return connections;
    }

    public IReadOnlyList<KeyValuePair<string, string>> History()
    {
        lock (_gate)
        {
            return _seen.OrderByDescending(pair => pair.Value).ToList();
        }
    }

    /// <summary>The words TCP and UDP stay the same in every language, and the state is passed through as it is.</summary>
    public static IReadOnlyList<ConnectionEntry> Parse(string text) => text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Where(parts => parts.Length >= 4 && parts[0] is "TCP" or "UDP")
        .Select(parts => parts[0] == "TCP" && parts.Length >= 5
            ? new ConnectionEntry("TCP", parts[1], parts[2], parts[3], ToId(parts[4]), string.Empty, IsWildcard(parts[2]))
            : new ConnectionEntry(parts[0], parts[1], parts[2], string.Empty, ToId(parts[^1]), string.Empty, true))
        .ToList();

    private static bool IsWildcard(string remote) => remote.EndsWith(":0", StringComparison.Ordinal) || remote.StartsWith('*');

    private static int ToId(string text) => int.TryParse(text, CultureInfo.InvariantCulture, out var id) ? id : 0;
}

public sealed record ExportFile(
    int Version,
    IReadOnlyList<string> Tweaks,
    SettingsUpdate? Choices,
    IReadOnlyList<string>? FocusApps,
    IReadOnlyList<string>? Apps);

public sealed record ImportResult(IReadOnlyList<string> Tweaks, IReadOnlyList<string> Apps, int Ignored);

/// <summary>Saves and loads a person's choices. A file can only name things already in the app's lists.</summary>
public static class SettingsFile
{
    public const int CurrentVersion = 1;
    private const int MaxBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Pretty = new(RpcProtocol.Json) { WriteIndented = true };

    public static string Build(IEnumerable<string> tweaks, AppSettings settings, IEnumerable<string> apps) =>
        JsonSerializer.Serialize(new ExportFile(CurrentVersion, tweaks.ToList(), settings.Choices, settings.FocusApps, apps.ToList()), Pretty);

    public static (ExportFile File, ImportResult Result) Read(string text, IReadOnlyCollection<string> knownTweaks, IReadOnlyCollection<string> knownApps)
    {
        if (text.Length > MaxBytes)
        {
            throw new CatalogRequestException("That file is too large to be a TuneForge file.");
        }

        ExportFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ExportFile>(text, RpcProtocol.Json);
        }
        catch (JsonException)
        {
            file = null;
        }

        if (file?.Tweaks is null || file.Version != CurrentVersion)
        {
            throw new CatalogRequestException("That isn't a TuneForge settings file.");
        }

        var tweaks = file.Tweaks.Where(knownTweaks.Contains).Distinct().ToList();
        var apps = (file.Apps ?? []).Where(knownApps.Contains).Distinct().ToList();
        var ignored = file.Tweaks.Count + (file.Apps?.Count ?? 0) - tweaks.Count - apps.Count;
        return (file, new ImportResult(tweaks, apps, ignored));
    }
}
