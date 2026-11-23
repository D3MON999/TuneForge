using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

/// <summary>Wear, temperature and hours as the drive reports them. Reading them needs admin rights.</summary>
public static class DriveHealth
{
    private const string Scope = @"root\Microsoft\Windows\Storage";

    public static string MediaName(int media, int bus) => media switch
    {
        3 => "Hard drive",
        4 or 5 => bus == 17 ? "NVMe SSD" : "SSD",
        _ => bus == 17 ? "NVMe SSD" : "Drive",
    };

    public static IReadOnlyList<DriveWear> Read()
    {
        var counters = new Dictionary<string, ManagementBaseObject>(StringComparer.OrdinalIgnoreCase);
        var drives = new List<DriveWear>();
        try
        {
            using var reliability = new ManagementObjectSearcher(Scope, "SELECT * FROM MSFT_StorageReliabilityCounter");
            foreach (var item in reliability.Get())
            {
                if (item["DeviceId"] is string id)
                {
                    counters[id] = item;
                }
            }

            using var disks = new ManagementObjectSearcher(Scope, "SELECT DeviceId, FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk");
            foreach (var disk in disks.Get())
            {
                var id = disk["DeviceId"] as string ?? string.Empty;
                counters.TryGetValue(id, out var counter);
                int? Number(string name) => counter?[name] is { } value && Convert.ToInt64(value, CultureInfo.InvariantCulture) is var number && number > 0 ? (int)number : null;
                drives.Add(new DriveWear(
                    disk["FriendlyName"] as string ?? $"Disk {id}",
                    MediaName(Convert.ToInt32(disk["MediaType"] ?? 0, CultureInfo.InvariantCulture), Convert.ToInt32(disk["BusType"] ?? 0, CultureInfo.InvariantCulture)),
                    counter?["Wear"] is { } wear ? Convert.ToInt32(wear, CultureInfo.InvariantCulture) : null,
                    Number("Temperature"),
                    Number("PowerOnHours"),
                    counter?["ReadErrorsUncorrected"] is { } errors ? Convert.ToInt64(errors, CultureInfo.InvariantCulture) : null));
            }
        }
        catch (ManagementException)
        {
            // Older storage drivers don't offer these counters.
        }

        return drives;
    }
}

/// <summary>What woke the PC, what's allowed to, and what's keeping it from sleeping.</summary>
public static partial class WakeTools
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    public static IReadOnlyList<WakeEvent> History(int limit = 15)
    {
        var wakes = new List<WakeEvent>();
        try
        {
            var query = new EventLogQuery("System", PathType.LogName, "*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            while (wakes.Count < limit && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    wakes.Add(new WakeEvent(
                        (record.TimeCreated ?? DateTime.MinValue).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                        WakeSource(record.ToXml())));
                }
            }
        }
        catch (Exception exception) when (exception is EventLogException or UnauthorizedAccessException)
        {
            // Nothing readable, so nothing to show.
        }

        return wakes;
    }

    public static string WakeSource(string eventXml)
    {
        var data = XDocument.Parse(eventXml).Descendants().Where(element => element.Name.LocalName == "Data")
            .ToDictionary(element => (string?)element.Attribute("Name") ?? string.Empty, element => element.Value.Trim());
        if (data.TryGetValue("WakeSourceText", out var text) && text.Length > 0)
        {
            return text;
        }

        return data.GetValueOrDefault("WakeSourceType") switch
        {
            "1" => "Power button",
            "3" => "A scheduled timer",
            "5" => "A device",
            _ => "Unknown",
        };
    }

    public static IReadOnlyList<string> DeviceQuery(IToolRunner runner, string kind) =>
        runner.Capture(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), ["/devicequery", kind], Limit).Text
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static WakeReport Read(IToolRunner runner)
    {
        var armed = DeviceQuery(runner, "wake_armed").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var devices = DeviceQuery(runner, "wake_programmable")
            .Select(name => new WakeDevice(name, armed.Contains(name)))
            .OrderByDescending(device => device.Armed)
            .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new WakeReport(History(), devices);
    }

    [GeneratedRegex(@"^([A-Z]+):$")]
    private static partial Regex Header();

    /// <summary>Parses powercfg's list of what's keeping the PC awake. The section names are the same in every language.</summary>
    public static IReadOnlyList<SleepBlocker> ParseRequests(string text)
    {
        var blockers = new List<SleepBlocker>();
        string? kind = null;
        SleepBlocker? pending = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (Header().Match(line) is { Success: true } header)
            {
                kind = header.Groups[1].Value switch
                {
                    "DISPLAY" or "ACTIVELOCKSCREEN" => "display",
                    "SYSTEM" or "EXECUTION" => "system",
                    "AWAYMODE" => "away",
                    _ => null,
                };
                continue;
            }

            if (kind is null || line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                var close = line.IndexOf(']');
                var who = close > 0 ? line[(close + 1)..].Trim() : line;
                if (who.Contains('\\') && who.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    who = Path.GetFileName(who);
                }

                pending = new SleepBlocker(kind, who, string.Empty);
                blockers.Add(pending);
            }
            else if (pending is not null && pending.Detail.Length == 0)
            {
                blockers[^1] = pending with { Detail = line };
                pending = null;
            }
        }

        return blockers.DistinctBy(blocker => (blocker.Kind, blocker.Who)).ToList();
    }

    public static IReadOnlyList<SleepBlocker> Blockers(IToolRunner runner) =>
        ParseRequests(runner.Capture(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), ["/requests"], Limit).Text);

    /// <summary>Only names Windows lists as wake-capable can be changed.</summary>
    public static OperationResult SetDevice(IToolRunner runner, WakeDeviceRequest request)
    {
        if (!DeviceQuery(runner, "wake_programmable").Contains(request.Name, StringComparer.OrdinalIgnoreCase))
        {
            throw new CatalogRequestException("That device can't wake the PC.");
        }

        var code = runner.Capture(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), [request.Armed ? "/deviceenablewake" : "/devicedisablewake", request.Name], Limit).ExitCode;
        return code == 0
            ? new OperationResult(true, request.Armed ? $"{request.Name} can wake the PC again." : $"{request.Name} won't wake the PC anymore.")
            : new OperationResult(false, "Windows didn't allow that change.");
    }
}

/// <summary>Scheduled tasks added by programs. Windows' own tasks and anti-cheat tasks are never offered.</summary>
public static class ScheduledTasks
{
    public static bool IsLocked(string name, string program) =>
        ProtectedProcesses.IsProtectedStartup(name, program)
        || ProtectedProcesses.IsProtected(Path.GetFileNameWithoutExtension(program))
        || name.Contains("TuneForge", StringComparison.OrdinalIgnoreCase)
        || program.Contains("Riot", StringComparison.OrdinalIgnoreCase)
        || program.Contains("EasyAntiCheat", StringComparison.OrdinalIgnoreCase)
        || program.Contains("BattlEye", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<ScheduledTaskInfo> List()
    {
        var tasks = new List<ScheduledTaskInfo>();
        try
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            service.Connect();
            Walk(service.GetFolder("\\"), tasks);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or ArgumentNullException)
        {
            // The Task Scheduler isn't available.
        }

        return tasks.OrderBy(task => task.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Walk(dynamic folder, List<ScheduledTaskInfo> tasks)
    {
        foreach (dynamic task in folder.GetTasks(1))
        {
            try
            {
                string path = task.Path;
                string name = task.Name;
                string program = string.Empty;
                dynamic definition = task.Definition;
                foreach (dynamic action in definition.Actions)
                {
                    if ((int)action.Type == 0)
                    {
                        program = Environment.ExpandEnvironmentVariables(((string?)action.Path ?? string.Empty).Trim('"'));
                        break;
                    }
                }

                if (program.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? author = definition.RegistrationInfo.Author;
                DateTime next = task.NextRunTime;
                tasks.Add(new ScheduledTaskInfo(
                    path,
                    name,
                    program,
                    string.IsNullOrWhiteSpace(author) || author.StartsWith('$') ? null : author,
                    (bool)task.Enabled,
                    next.Year < 2000 ? null : next.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    IsLocked(name, program)));
            }
            catch (COMException)
            {
                // A task you can't read is skipped.
            }
        }

        foreach (dynamic child in folder.GetFolders(0))
        {
            string path = child.Path;
            if (!path.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase))
            {
                Walk(child, tasks);
            }
        }
    }

    public static OperationResult Set(IProcessRunner runner, TaskSetRequest request)
    {
        var task = List().FirstOrDefault(item => item.Path.Equals(request.Path, StringComparison.OrdinalIgnoreCase))
            ?? throw new CatalogRequestException("That task no longer exists.");
        if (task.Locked)
        {
            throw new CatalogRequestException($"{task.Name} is locked, because a game or TuneForge needs it.");
        }

        return runner.Run("schtasks.exe", ["/change", "/tn", task.Path, request.Enabled ? "/enable" : "/disable"], TimeSpan.FromSeconds(30)) == 0
            ? new OperationResult(true, request.Enabled ? $"{task.Name} is back on." : $"{task.Name} is off. Turn it back on here any time.")
            : new OperationResult(false, $"Windows didn't let TuneForge change {task.Name}.");
    }
}

/// <summary>
/// Entries other programs added to the right-click menu. Turning one off uses Windows' own switches
/// (the blocked list for shell extensions, LegacyDisable for commands), journaled so History can undo it.
/// </summary>
public static partial class RightClickMenu
{
    private const string BlockedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    private static readonly (string Class, string Where)[] Handlers =
    [
        ("*", "files"), ("AllFilesystemObjects", "files"), ("Directory", "folders"), ("Folder", "folders"), (@"Directory\Background", "background"),
    ];

    private static readonly (string Class, string Where)[] Verbs = [("*", "files"), ("Directory", "folders"), (@"Directory\Background", "background")];

    [GeneratedRegex(@"^\{[0-9A-Fa-f-]{36}\}$")]
    private static partial Regex Clsid();

    private static bool IsWindowsFile(string? path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return string.IsNullOrEmpty(path) || path.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
    }

    private static string Clean(string text) => text.Replace("&", string.Empty, StringComparison.Ordinal).Trim();

    public static IReadOnlyList<MenuEntry> List()
    {
        var entries = new List<MenuEntry>();
        using var blocked = Registry.LocalMachine.OpenSubKey(BlockedKey);
        var blockedIds = (blocked?.GetValueNames() ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (cls, where) in Handlers)
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"{cls}\shellex\ContextMenuHandlers");
            foreach (var name in key?.GetSubKeyNames() ?? [])
            {
                using var handler = key!.OpenSubKey(name);
                var clsid = handler?.GetValue(null) as string is { } value && Clsid().IsMatch(value) ? value : Clsid().IsMatch(name) ? name : null;
                if (clsid is null)
                {
                    continue;
                }

                using var server = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}\InprocServer32");
                var file = server?.GetValue(null) is string dll ? Environment.ExpandEnvironmentVariables(dll.Trim('"')) : null;
                if (IsWindowsFile(file))
                {
                    continue;
                }

                using var described = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}");
                var title = described?.GetValue(null) as string;
                var shown = !string.IsNullOrWhiteSpace(title) ? title : !Clsid().IsMatch(name) ? name : Path.GetFileNameWithoutExtension(file!);
                entries.Add(new MenuEntry($"shellex:{clsid.ToUpperInvariant()}", Clean(shown), where, file, !blockedIds.Contains(clsid)));
            }
        }

        foreach (var (cls, where) in Verbs)
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"{cls}\shell");
            foreach (var verb in key?.GetSubKeyNames() ?? [])
            {
                using var entry = key!.OpenSubKey(verb);
                using var command = entry?.OpenSubKey("command");
                if (command?.GetValue(null) is not string text || StartupLaunch.Split(text) is not { } split || IsWindowsFile(split.File) || !File.Exists(split.File))
                {
                    continue;
                }

                var title = entry!.GetValue("MUIVerb") as string ?? entry.GetValue(null) as string;
                var hive = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{cls}\shell\{verb}") is { } mine ? Dispose(mine, "HKCU") : "HKLM";
                entries.Add(new MenuEntry(
                    $"verb:{hive}:{cls}:{verb}",
                    Clean(string.IsNullOrWhiteSpace(title) || title.StartsWith('@') ? verb : title),
                    where,
                    split.File,
                    entry.GetValue("LegacyDisable") is null));
            }
        }

        return entries.DistinctBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase).OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Dispose(RegistryKey key, string value)
    {
        key.Dispose();
        return value;
    }

    /// <summary>Builds the change for an entry that really exists. Any other id is refused.</summary>
    public static TweakItem BuildChange(MenuSetRequest request)
    {
        var entry = List().FirstOrDefault(item => item.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase))
            ?? throw new CatalogRequestException("That menu entry no longer exists.");
        var empty = JsonSerializer.SerializeToElement(string.Empty);
        RegistryAction action;
        if (entry.Id.StartsWith("shellex:", StringComparison.Ordinal))
        {
            var clsid = entry.Id["shellex:".Length..];
            action = request.Enabled
                ? new RegistryAction("HKLM", BlockedKey, clsid, "string", null, Delete: true)
                : new RegistryAction("HKLM", BlockedKey, clsid, "string", empty);
        }
        else
        {
            var parts = entry.Id.Split(':', 4);
            var path = $@"Software\Classes\{parts[2]}\shell\{parts[3]}";
            action = request.Enabled
                ? new RegistryAction(parts[1], path, "LegacyDisable", "string", null, Delete: true)
                : new RegistryAction(parts[1], path, "LegacyDisable", "string", empty);
        }

        return new TweakItem($"menu:{entry.Id}", entry.Name, "menu", null, "low", null, "none", [], null, null, "TuneForge", [action]);
    }
}

/// <summary>Checks whose digital signature a running program carries. It only reads the program files.</summary>
public static class SignatureCheck
{
    private const uint QueryLimited = 0x1000;
    private static readonly Guid VerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static IReadOnlyList<ProcessSignature> Check()
    {
        var processes = Process.GetProcesses();
        try
        {
            if (ProtectedProcesses.AnyGameRunning(processes.Select(process => process.ProcessName)))
            {
                throw new CatalogRequestException("Close your game first. TuneForge stays away from running programs while a protected game is open.");
            }

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var seen = new Dictionary<string, ProcessSignature>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in processes)
            {
                if (ProcessService.IsLocked(process.ProcessName) || NearAntiCheat(process.ProcessName) || ImagePath(process.Id) is not { } path || seen.ContainsKey(path))
                {
                    continue;
                }

                seen[path] = path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
                    ? new ProcessSignature(process.ProcessName, path, "windows", "Microsoft Windows")
                    : path.Contains(@"WindowsApps\", StringComparison.OrdinalIgnoreCase) ? new ProcessSignature(process.ProcessName, path, "signed", "Microsoft Store app")
                    : Verify(path) ? new ProcessSignature(process.ProcessName, path, "signed", Publisher(path)) : new ProcessSignature(process.ProcessName, path, "unsigned", null);
            }

            return seen.Values.OrderBy(item => item.Status switch { "unsigned" => 0, "signed" => 1, _ => 2 }).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>Games and anti-cheat are never opened, not even to read where they live.</summary>
    public static bool NearAntiCheat(string name) =>
        ProtectedProcesses.IsProtected(name)
        || new[] { "Riot", "Vanguard", "vgc", "vgk", "EasyAntiCheat", "BattlEye", "BEService", "FACEIT", "Valorant", "League" }.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static string? ImagePath(int id)
    {
        var handle = OpenProcess(QueryLimited, false, (uint)id);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static string? Publisher(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057
            var subject = X509Certificate.CreateFromSignedFile(path).Subject;
#pragma warning restore SYSLIB0057
            var name = subject.Split(',').Select(part => part.Trim()).FirstOrDefault(part => part.StartsWith("CN=", StringComparison.Ordinal));
            return name?[3..].Trim('"');
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>True when the file's own signature checks out, without going online.</summary>
    public static bool Verify(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        var dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustData>());
        try
        {
            Marshal.StructureToPtr(file, filePointer, false);
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(),
                UiChoice = 2,
                UnionChoice = 1,
                File = filePointer,
                ProviderFlags = 0x80,
            };
            Marshal.StructureToPtr(data, dataPointer, false);
            return WinVerifyTrust(-1, VerifyV2, dataPointer) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<TrustFile>(filePointer);
            Marshal.FreeHGlobal(filePointer);
            Marshal.FreeHGlobal(dataPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public uint Size;
        public string Path;
        public nint Handle;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint File;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(nint window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, nint data);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

/// <summary>Blocks a program from the internet with Windows Firewall rules that TuneForge names and groups as its own.</summary>
public sealed class FirewallBlocks(IToolRunner runner)
{
    private const string Group = "TuneForge";
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(1);
    private static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public IReadOnlyList<FirewallBlock> List() =>
        runner.Capture(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", $"Get-NetFirewallRule -Group '{Group}' -ErrorAction SilentlyContinue | Get-NetFirewallApplicationFilter | Select-Object -ExpandProperty Program -Unique"], Limit).Text
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(program => new FirewallBlock(program))
            .ToList();

    /// <summary>The path goes into a PowerShell command in single quotes, so it must be a real program with no quote in it.</summary>
    public static void Check(string program)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var name = Path.GetFileNameWithoutExtension(program);
        if (!Path.IsPathFullyQualified(program) || !program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || program.Any(character => character is '\'' or '`' || char.IsControl(character)))
        {
            throw new CatalogRequestException("Pick a program file (.exe).");
        }

        if (program.StartsWith(windows, StringComparison.OrdinalIgnoreCase) || ProcessService.IsLocked(name) || ProtectedProcesses.IsProtected(name))
        {
            throw new CatalogRequestException($"{Path.GetFileName(program)} is part of Windows, a game's anti-cheat or TuneForge, so it can't be blocked.");
        }
    }

    public OperationResult Block(string program)
    {
        Check(program);
        if (!File.Exists(program))
        {
            throw new CatalogRequestException("That program doesn't exist.");
        }

        var file = Path.GetFileName(program);
        var script = string.Join("; ",
            Remove(program),
            $"New-NetFirewallRule -DisplayName 'TuneForge: block {file}' -Group '{Group}' -Direction Outbound -Program '{program}' -Action Block | Out-Null",
            $"New-NetFirewallRule -DisplayName 'TuneForge: block {file}' -Group '{Group}' -Direction Inbound -Program '{program}' -Action Block | Out-Null");
        return runner.Capture(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", script], Limit).ExitCode == 0
            ? new OperationResult(true, $"{file} can't reach the internet now. Unblock it here any time.")
            : new OperationResult(false, "Windows Firewall didn't accept the rule.");
    }

    public OperationResult Unblock(string program)
    {
        Check(program);
        return runner.Capture(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", Remove(program)], Limit).ExitCode == 0
            ? new OperationResult(true, $"{Path.GetFileName(program)} can reach the internet again.")
            : new OperationResult(false, "Windows Firewall didn't remove the rule.");
    }

    private static string Remove(string program) =>
        $"Get-NetFirewallRule -Group '{Group}' -ErrorAction SilentlyContinue | Where-Object {{ ($_ | Get-NetFirewallApplicationFilter).Program -eq '{program}' }} | Remove-NetFirewallRule";
}

/// <summary>Pauses Windows Update the same way Settings does: up to 5 weeks, then it resumes by itself.</summary>
public static class UpdatePause
{
    public const string Key = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    private static readonly string[] Starts = ["PauseUpdatesStartTime", "PauseFeatureUpdatesStartTime", "PauseQualityUpdatesStartTime"];
    private static readonly string[] Ends = ["PauseUpdatesExpiryTime", "PauseFeatureUpdatesEndTime", "PauseQualityUpdatesEndTime"];

    public static UpdatePauseStatus Read(DateTime nowUtc)
    {
        using var key = Registry.LocalMachine.OpenSubKey(Key);
        return key?.GetValue("PauseUpdatesExpiryTime") is string text
            && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var until)
            && until > nowUtc
            ? new UpdatePauseStatus(true, until.ToString("O", CultureInfo.InvariantCulture))
            : new UpdatePauseStatus(false, null);
    }

    /// <summary>0 weeks resumes updates.</summary>
    public static OperationResult Set(int weeks, DateTime nowUtc)
    {
        if (weeks is < 0 or > 5)
        {
            throw new CatalogRequestException("Updates can be paused for 1 to 5 weeks.");
        }

        using var key = Registry.LocalMachine.CreateSubKey(Key, writable: true);
        if (weeks == 0)
        {
            foreach (var name in Starts.Concat(Ends))
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }

            return new OperationResult(true, "Updates are back on. Windows will check for them soon.");
        }

        var start = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var end = nowUtc.AddDays(weeks * 7).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        foreach (var name in Starts)
        {
            key.SetValue(name, start, RegistryValueKind.String);
        }

        foreach (var name in Ends)
        {
            key.SetValue(name, end, RegistryValueKind.String);
        }

        return new OperationResult(true, weeks == 1 ? "Updates are paused for 1 week." : $"Updates are paused for {weeks} weeks.");
    }
}

/// <summary>Saves every installed driver to a folder, and installs drivers from one, using Windows' own pnputil.</summary>
public sealed class AllDriversBackup(IProcessRunner runner, string profile, int osBuild)
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(20);

    public string FolderFor(DateTime now) =>
        Path.Combine(profile, "Documents", "TuneForge Backups", $"Drivers {now.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture)}");

    public DriverExportResult Export(DateTime now)
    {
        if (osBuild < 19041)
        {
            return new DriverExportResult(false, "This needs Windows 10 version 2004 or newer.", null);
        }

        var folder = FolderFor(now);
        Directory.CreateDirectory(folder);
        var code = runner.Run("pnputil.exe", ["/export-driver", "*", folder], Limit);
        var count = Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories).Count();
        return code == 0 && count > 0
            ? new DriverExportResult(true, $"Saved {count} drivers. Keep this folder somewhere safe, like a USB stick, before reinstalling Windows.", folder)
            : new DriverExportResult(false, "The drivers couldn't be saved. Check there's a few GB free on drive C.", null);
    }

    public OperationResult Import(string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)
            || !Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories).Any())
        {
            throw new CatalogRequestException("That folder has no drivers in it.");
        }

        var code = runner.Run("pnputil.exe", ["/add-driver", Path.Combine(folder, "*.inf"), "/subdirs", "/install"], Limit);
        return code switch
        {
            0 => new OperationResult(true, "The drivers are installed."),
            3010 => new OperationResult(true, "The drivers are installed. Restart to finish.", true),
            259 => new OperationResult(true, "Windows added the drivers. None of them were needed for the devices in this PC."),
            _ => new OperationResult(false, "Some drivers couldn't be installed. Windows only accepts signed drivers."),
        };
    }
}
