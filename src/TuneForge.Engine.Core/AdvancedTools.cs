using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

public sealed record ToolStep(string Tool, string[] Arguments);

public interface IAdvancedState
{
    IReadOnlySet<string> Enabled();

    void Save(IReadOnlySet<string> ids);
}

/// <summary>Remembers which switches TuneForge turned on. It sits beside the journal, so only admins can write it.</summary>
public sealed class FileAdvancedState(string path) : IAdvancedState
{
    public IReadOnlySet<string> Enabled()
    {
        try
        {
            return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new HashSet<string>();
        }
    }

    public void Save(IReadOnlySet<string> ids) => File.WriteAllText(path, JsonSerializer.Serialize(ids));
}

public interface IAdapterSettings
{
    void SetNetbios(bool off);

    void SetNagle(bool off);
}

/// <summary>Writes the per-adapter settings Windows keeps for each network connection.</summary>
public sealed class WindowsAdapterSettings : IAdapterSettings
{
    private const string NetbiosKey = @"SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces";
    private const string TcpKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    public void SetNetbios(bool off)
    {
        using var root = Registry.LocalMachine.OpenSubKey(NetbiosKey);
        foreach (var name in root?.GetSubKeyNames() ?? [])
        {
            using var adapter = root!.OpenSubKey(name, writable: true);
            adapter?.SetValue("NetbiosOptions", off ? 2 : 0, RegistryValueKind.DWord);
        }
    }

    public void SetNagle(bool off)
    {
        using var root = Registry.LocalMachine.OpenSubKey(TcpKey);
        foreach (var name in root?.GetSubKeyNames() ?? [])
        {
            using var adapter = root!.OpenSubKey(name, writable: true);
            if (off)
            {
                adapter?.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                adapter?.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);
            }
            else
            {
                adapter?.DeleteValue("TcpAckFrequency", false);
                adapter?.DeleteValue("TCPNoDelay", false);
            }
        }
    }
}

/// <summary>
/// Windows settings that are changed by a tool rather than a registry value. Each one is a fixed
/// list of commands, and these switches run only when someone turns them on themselves.
/// </summary>
public sealed class AdvancedService(IProcessRunner runner, IAdvancedState state, IAdapterSettings adapters)
{
    private const int RestartRequired = 3010;
    private const string UsbSubgroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string UsbSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(20);

    private static readonly string[] NvidiaTasks =
    [
        "NvTmRepOnLogon_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}",
        "NvTmRep_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}",
        "NvTmMon_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}",
    ];

    private static ToolStep Step(string tool, params string[] arguments) => new(tool, arguments);

    private static readonly Definition[] Definitions =
    [
        new("hibernation", "Disable hibernation", "Removes the hibernation file, which frees several gigabytes on your Windows drive.", "Fast startup and Hibernate stop working.", "medium", false, true,
            [Step("powercfg.exe", "/hibernate", "off")], [Step("powercfg.exe", "/hibernate", "on")]),
        new("usb-suspend", "Disable USB power saving", "Keeps USB devices powered, which can stop mice and headsets dropping out.", "Laptops use slightly more battery.", "low", false, true,
            [Step("powercfg.exe", "/setacvalueindex", "SCHEME_CURRENT", UsbSubgroup, UsbSuspend, "0"), Step("powercfg.exe", "/setdcvalueindex", "SCHEME_CURRENT", UsbSubgroup, UsbSuspend, "0"), Step("powercfg.exe", "/setactive", "SCHEME_CURRENT")],
            [Step("powercfg.exe", "/setacvalueindex", "SCHEME_CURRENT", UsbSubgroup, UsbSuspend, "1"), Step("powercfg.exe", "/setdcvalueindex", "SCHEME_CURRENT", UsbSubgroup, UsbSuspend, "1"), Step("powercfg.exe", "/setactive", "SCHEME_CURRENT")]),
        new("hpet", "Prefer the platform tick", "Changes which hardware timer Windows uses. Some players report lower latency.", "Results vary by PC, and it can lower performance. Back up with a restore point first.", "high", true, true,
            [Step("bcdedit.exe", "/set", "useplatformtick", "yes")], [Step("bcdedit.exe", "/deletevalue", "useplatformtick")]),
        new("dynamic-tick", "Disable dynamic tick", "Keeps the system timer running steadily instead of pausing it when the PC is idle.", "Uses more power, and can shorten laptop battery life.", "high", true, true,
            [Step("bcdedit.exe", "/set", "disabledynamictick", "yes")], [Step("bcdedit.exe", "/deletevalue", "disabledynamictick")]),
        new("legacy-boot-menu", "Legacy F8 boot menu", "Brings back the old boot menu, so F8 opens Safe Mode during startup.", "Startup takes a little longer. It may not appear on PCs with very fast boot.", "medium", true, true,
            [Step("bcdedit.exe", "/set", "{current}", "bootmenupolicy", "legacy")], [Step("bcdedit.exe", "/set", "{current}", "bootmenupolicy", "standard")]),
        new("ntfs-tuning", "NTFS tuning", "Stops Windows updating last-opened times and creating short 8.3 file names, which reduces disk writes.", "Very old programs that need short file names may break.", "medium", true, true,
            [Step("fsutil.exe", "behavior", "set", "disablelastaccess", "1"), Step("fsutil.exe", "behavior", "set", "disable8dot3", "1")],
            [Step("fsutil.exe", "behavior", "set", "disablelastaccess", "2"), Step("fsutil.exe", "behavior", "set", "disable8dot3", "2")]),
        new("pagefile-encryption", "Disable page file encryption", "Stops Windows encrypting the page file, which saves a little CPU time.", "Data held in the page file is no longer encrypted on disk.", "medium", true, true,
            [Step("fsutil.exe", "behavior", "set", "encryptpagingfile", "0")], [Step("fsutil.exe", "behavior", "set", "encryptpagingfile", "1")]),
        new("reserved-storage", "Disable Reserved Storage", "Gives back the space Windows keeps for updates, usually about 7 GB.", "Updates can fail when your drive is nearly full.", "medium", true, true,
            [Step("dism.exe", "/Online", "/Set-ReservedStorageState", "/State:Disabled")], [Step("dism.exe", "/Online", "/Set-ReservedStorageState", "/State:Enabled")]),
        new("tcp-tuning", "TCP tuning", "Turns on CTCP, ECN and receive side scaling, which can improve throughput on some connections.", "Some older routers handle ECN badly. Turn this off if your connection gets worse.", "medium", false, true,
            [Step("netsh.exe", "int", "tcp", "set", "supplemental", "template=internet", "congestionprovider=ctcp"), Step("netsh.exe", "int", "tcp", "set", "global", "ecncapability=enabled"), Step("netsh.exe", "int", "tcp", "set", "global", "rss=enabled")],
            [Step("netsh.exe", "int", "tcp", "set", "supplemental", "template=internet", "congestionprovider=default"), Step("netsh.exe", "int", "tcp", "set", "global", "ecncapability=default")]),
        new("teredo", "Disable Teredo", "Turns off an old way of tunnelling IPv6 over IPv4.", "Some older Xbox and peer-to-peer features may stop working.", "medium", false, true,
            [Step("netsh.exe", "interface", "teredo", "set", "state", "disabled")], [Step("netsh.exe", "interface", "teredo", "set", "state", "default")]),
        new("nvidia-telemetry", "Disable NVIDIA telemetry tasks", "Turns off NVIDIA's scheduled telemetry tasks. Newer drivers may not have them.", "None expected. GeForce features keep working.", "low", false, true,
            NvidiaTasks.Select(task => Step("schtasks.exe", "/change", "/tn", task, "/disable")).ToArray(),
            NvidiaTasks.Select(task => Step("schtasks.exe", "/change", "/tn", task, "/enable")).ToArray()) { IgnoreFailures = true },
        new("openssh-server", "OpenSSH server", "Installs Windows' SSH server, so you can sign in to this PC from another computer.", "It opens a way into your PC. Use a strong password and only turn it on if you need it.", "high", false, true,
            [Step("dism.exe", "/Online", "/Add-Capability", "/CapabilityName:OpenSSH.Server~~~~0.0.1.0")], [Step("dism.exe", "/Online", "/Remove-Capability", "/CapabilityName:OpenSSH.Server~~~~0.0.1.0")]),
        new("netbios", "Disable NetBIOS", "Turns off an old network naming protocol on all your connections.", "Very old network devices may no longer find this PC.", "medium", true, true, [], []) { Adapter = (settings, on) => settings.SetNetbios(on) },
        new("nagle", "Disable Nagle's algorithm", "Sends small network packets straight away, which can lower latency in some older games.", "Slightly more network traffic, and most modern games already do this themselves.", "medium", true, true, [], []) { Adapter = (settings, on) => settings.SetNagle(on) },
        new("remove-onedrive", "Uninstall OneDrive", "Removes the OneDrive app. Your files stay online and in your OneDrive folder.", "Files stop syncing. To get it back, install OneDrive from Apps > Install apps.", "high", false, false,
            [Step("OneDriveSetup.exe", "/uninstall")], []),
    ];

    public IReadOnlyList<AdvancedEntry> List()
    {
        var enabled = state.Enabled();
        return Definitions.Select(item => new AdvancedEntry(item.Id, item.Title, item.Description, item.Warning, item.Risk, item.Restart, item.Reversible, enabled.Contains(item.Id))).ToList();
    }

    public OperationResult Set(AdvancedSetRequest request)
    {
        var item = Definitions.FirstOrDefault(entry => entry.Id == request.Id)
            ?? throw new CatalogRequestException($"Unknown option: {request.Id}");
        if (!request.Enabled && !item.Reversible)
        {
            throw new CatalogRequestException($"{item.Title} can't be turned back on here.");
        }

        item.Adapter?.Invoke(adapters, request.Enabled);
        var restart = item.Restart;
        foreach (var step in request.Enabled ? item.On : item.Off)
        {
            var code = runner.Run(step.Tool, step.Arguments, Limit);
            restart |= code == RestartRequired;
            if (code != 0 && code != RestartRequired && !item.IgnoreFailures)
            {
                return new OperationResult(false, $"Windows couldn't change {item.Title} (code {code.ToString(CultureInfo.InvariantCulture)}).", restart);
            }
        }

        var ids = state.Enabled().ToHashSet();
        if (request.Enabled)
        {
            ids.Add(item.Id);
        }
        else
        {
            ids.Remove(item.Id);
        }

        state.Save(ids);
        return new OperationResult(true, restart ? $"{item.Title} changes after you restart." : $"{item.Title} is updated.", restart);
    }

    private sealed record Definition(
        string Id,
        string Title,
        string Description,
        string Warning,
        string Risk,
        bool Restart,
        bool Reversible,
        ToolStep[] On,
        ToolStep[] Off)
    {
        public bool IgnoreFailures { get; init; }

        public Action<IAdapterSettings, bool>? Adapter { get; init; }
    }
}

public interface ISecurityReader
{
    SecurityStatus Read();
}

/// <summary>Shows protection status only. Null means Windows didn't say, usually because another antivirus is in charge.</summary>
public sealed class WindowsSecurityReader : ISecurityReader
{
    private const string DefenderScope = @"root\Microsoft\Windows\Defender";

    public SecurityStatus Read() => new(
        Bool(DefenderScope, "SELECT AntivirusEnabled FROM MSFT_MpComputerStatus", "AntivirusEnabled"),
        Bool(DefenderScope, "SELECT RealTimeProtectionEnabled FROM MSFT_MpComputerStatus", "RealTimeProtectionEnabled"),
        Folder(),
        Firewall(),
        BitLocker());

    private static bool? First(string scope, string query, Func<ManagementBaseObject, bool?> read)
    {
        try
        {
            using var search = new ManagementObjectSearcher(scope, query);
            var item = search.Get().Cast<ManagementBaseObject>().FirstOrDefault();
            return item is null ? null : read(item);
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or COMException)
        {
            return null;
        }
    }

    private static bool? Bool(string scope, string query, string name) => First(scope, query, item => item[name] as bool?);

    private static bool? Folder() =>
        First(DefenderScope, "SELECT EnableControlledFolderAccess FROM MSFT_MpPreference", item =>
            item["EnableControlledFolderAccess"] is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1 : (bool?)null);

    private static bool? Firewall()
    {
        try
        {
            using var search = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT Enabled FROM MSFT_NetFirewallProfile");
            var profiles = search.Get().Cast<ManagementBaseObject>().ToList();
            return profiles.Count == 0 ? null : profiles.All(profile => Convert.ToInt32(profile["Enabled"], CultureInfo.InvariantCulture) == 1);
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or COMException)
        {
            return null;
        }
    }

    /// <summary>Windows' file manager reports this without admin rights: 1 and 3 mean protection is on.</summary>
    private static bool? BitLocker()
    {
        var drive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (drive is null || type is null)
        {
            return null;
        }

        try
        {
            dynamic shell = Activator.CreateInstance(type)!;
            var value = shell.NameSpace(drive).Self.ExtendedProperty("System.Volume.BitLockerProtection");
            return value is null ? null : (int)value is 1 or 3 or 5;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }
}

/// <summary>Only ever turns protection on.</summary>
public sealed class SecurityService(IProcessRunner runner)
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);

    public OperationResult TurnOn(SecuritySetRequest request) => request.Id switch
    {
        "firewall" => Report(runner.Run("netsh.exe", ["advfirewall", "set", "allprofiles", "state", "on"], Limit), "The firewall is on."),
        "controlled-folders" => Report(
            runner.Run(@"WindowsPowerShell\v1.0\powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "Set-MpPreference -EnableControlledFolderAccess Enabled"], Limit),
            "Controlled folder access is on. Some apps may need permission to save to your folders."),
        _ => throw new CatalogRequestException("Unknown protection."),
    };

    private static OperationResult Report(int code, string done) =>
        code == 0 ? new OperationResult(true, done) : new OperationResult(false, "Windows couldn't turn this on. Another security app may be in charge.");
}

/// <summary>Windows tools that open in their own window. Only the fixed list here can be opened.</summary>
public static class WindowsToolLinks
{
    public static IReadOnlyList<(ToolLink Link, string File, string Arguments)> All { get; } =
    [
        (new("memory-test", "Windows Memory Diagnostic", "Tests your RAM after a restart. Use it if you get random crashes."), "mdsched.exe", ""),
        (new("disk-cleanup", "Disk Cleanup", "Windows' own cleaner, for a deeper clean than the Cleanup page."), "cleanmgr.exe", ""),
        (new("control-panel", "Control Panel", "The classic settings window."), "control.exe", ""),
        (new("network-connections", "Network Connections", "See and change your network adapters."), "control.exe", "ncpa.cpl"),
        (new("power-options", "Power Options", "Change power plan settings in detail."), "control.exe", "powercfg.cpl"),
        (new("sound", "Sound", "Change playback and recording devices."), "control.exe", "mmsys.cpl"),
        (new("system-properties", "System Properties", "Change your PC's name, virtual memory and restore settings."), "control.exe", "sysdm.cpl"),
        (new("programs", "Programs and Features", "Uninstall classic desktop programs."), "control.exe", "appwiz.cpl"),
        (new("device-manager", "Device Manager", "See your hardware and update drivers."), "mmc.exe", "devmgmt.msc"),
        (new("disk-management", "Disk Management", "Format, resize and manage drives."), "mmc.exe", "diskmgmt.msc"),
        (new("services", "Services", "The full list of Windows services."), "mmc.exe", "services.msc"),
        (new("event-viewer", "Event Viewer", "Windows' full logs."), "mmc.exe", "eventvwr.msc"),
        (new("task-scheduler", "Task Scheduler", "See and change scheduled tasks."), "mmc.exe", "taskschd.msc"),
        (new("registry-editor", "Registry Editor", "Advanced. Wrong changes can stop Windows working."), "regedit.exe", ""),
    ];

    public static void Open(string id)
    {
        var (_, file, arguments) = All.FirstOrDefault(tool => tool.Link.Id == id);
        if (file is null)
        {
            throw new CatalogRequestException("Unknown tool.");
        }

        var path = Path.Combine(file == "regedit.exe" ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : Environment.SystemDirectory, file);
        using var _ = Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = true });
    }
}

/// <summary>Running programs, for the process list. System processes are locked.</summary>
public sealed class ProcessService
{
    private static readonly string[] Locked =
    [
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "fontdrvhost",
        "dwm", "explorer", "MsMpEng", "NisSrv", "SecurityHealthService", "spoolsv", "sihost", "ctfmon", "TuneForge", "TuneForge.Engine", "Memory Compression",
    ];

    public IReadOnlyList<ProcessEntry> List()
    {
        var entries = new List<ProcessEntry>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    entries.Add(new ProcessEntry(process.Id, process.ProcessName, process.WorkingSet64, process.MainWindowHandle != 0, IsLocked(process.ProcessName)));
                }
                catch (InvalidOperationException)
                {
                    // It exited while being read.
                }
            }
        }

        return entries.OrderByDescending(entry => entry.MemoryBytes).ToList();
    }

    public static bool IsLocked(string name) => Locked.Contains(name, StringComparer.OrdinalIgnoreCase) || ProtectedProcesses.IsProtected(name);

    public OperationResult End(ProcessEndRequest request)
    {
        if (IsLocked(request.Name))
        {
            throw new CatalogRequestException($"{request.Name} is locked because Windows or a game's anti-cheat needs it.");
        }

        try
        {
            using var process = Process.GetProcessById(request.Id);

            // The name must still match, in case Windows reused the ID for something else.
            if (!process.ProcessName.Equals(request.Name, StringComparison.OrdinalIgnoreCase))
            {
                return new OperationResult(false, $"{request.Name} has already closed.");
            }

            process.Kill();
            return new OperationResult(true, $"{request.Name} was closed.");
        }
        catch (ArgumentException)
        {
            return new OperationResult(false, $"{request.Name} has already closed.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return new OperationResult(false, $"Windows wouldn't let TuneForge close {request.Name}.");
        }
    }
}
