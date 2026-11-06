using System.Globalization;
using System.Management;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

public interface IFeatureStateReader
{
    /// <summary>Maps each Windows feature name to whether it's on. Missing names aren't available.</summary>
    IReadOnlyDictionary<string, bool> Read();
}

public sealed class WmiFeatureStateReader : IFeatureStateReader
{
    private const uint Enabled = 1;

    public IReadOnlyDictionary<string, bool> Read()
    {
        var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        using var search = new ManagementObjectSearcher("SELECT Name, InstallState FROM Win32_OptionalFeature");
        foreach (var feature in search.Get())
        {
            using (feature)
            {
                states[(string)feature["Name"]] = (uint)feature["InstallState"] == Enabled;
            }
        }

        return states;
    }
}

/// <summary>Turns optional Windows features on or off. Only the features listed here can be changed.</summary>
public sealed class FeatureService(IProcessRunner runner, IFeatureStateReader reader)
{
    private const int RestartRequired = 3010;
    private static readonly TimeSpan TimeLimit = TimeSpan.FromMinutes(30);

    private static readonly Definition[] Definitions =
    [
        new("sandbox", "Windows Sandbox", "A throwaway copy of Windows for testing programs safely.", ["Containers-DisposableClientVM"]),
        new("wsl", "Windows Subsystem for Linux", "Runs Linux tools inside Windows.", ["Microsoft-Windows-Subsystem-Linux", "VirtualMachinePlatform"]),
        new("hyper-v", "Hyper-V", "Runs virtual machines. Some games' anti-cheat may not like it.", ["Microsoft-Hyper-V-All"]),
        new("dotnet", ".NET Framework 3.5", "Needed by some older programs.", ["NetFx3"]),
        new("legacy-media", "Legacy media components", "DirectPlay and Windows Media Player, for older games and apps.", ["LegacyComponents", "DirectPlay", "MediaPlayback"]),
        new("nfs", "Network File System", "Connects to Linux and NAS file shares.", ["ServicesForNFS-ClientOnly", "ClientForNFS-Infrastructure"]),
    ];

    public IReadOnlyList<FeatureEntry> List()
    {
        var states = reader.Read();
        return Definitions.Select(definition =>
        {
            var state = !definition.Features.All(states.ContainsKey) ? "unavailable"
                : definition.Features.All(name => states[name]) ? "enabled"
                : "disabled";
            return new FeatureEntry(definition.Id, definition.Title, definition.Note, state);
        }).ToList();
    }

    public OperationResult Set(FeatureSetRequest request)
    {
        var definition = Definitions.FirstOrDefault(candidate => candidate.Id == request.Id)
            ?? throw new CatalogRequestException($"Unknown Windows feature: {request.Id}");

        var restart = false;
        foreach (var feature in definition.Features)
        {
            string[] arguments = request.Enabled
                ? ["/Online", "/Enable-Feature", $"/FeatureName:{feature}", "/All", "/NoRestart"]
                : ["/Online", "/Disable-Feature", $"/FeatureName:{feature}", "/NoRestart"];

            var code = runner.Run("dism.exe", arguments, TimeLimit);
            if (code != 0 && code != RestartRequired)
            {
                return new OperationResult(false, $"Windows couldn't change {definition.Title} (code {code.ToString(CultureInfo.InvariantCulture)}).", restart);
            }

            restart |= code == RestartRequired;
        }

        return new OperationResult(
            true,
            restart ? $"{definition.Title} changes after you restart." : $"{definition.Title} is updated.",
            restart);
    }

    private sealed record Definition(string Id, string Title, string Note, string[] Features);
}

/// <summary>Runs Windows' own repair tools. Each repair is a fixed list of commands.</summary>
public sealed class RepairService(IProcessRunner runner, IDirectoryMover folders)
{
    private static readonly TimeSpan Short = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(90);
    private static readonly string[] UpdateServices = ["wuauserv", "bits", "cryptsvc", "msiserver"];

    public static IReadOnlyList<RepairInfo> Available { get; } =
    [
        new("system-files", "Check system files", "Finds and repairs damaged Windows files.", true),
        new("windows-image", "Repair Windows image", "Downloads clean copies of damaged components. Run this before Check system files if that one fails.", true),
        new("network", "Reset network", "Clears DNS and resets network settings. Wi-Fi may reconnect and you'll need to restart.", false),
        new("windows-update", "Reset Windows Update", "Clears Windows Update's cache when updates get stuck. Update history may look empty afterward.", false),
        new("time", "Fix the clock", "Syncs the time with Windows' time server.", false),
    ];

    public OperationResult Run(RepairRequest request) => request.Id switch
    {
        "system-files" => SystemFiles(),
        "windows-image" => WindowsImage(),
        "network" => Network(),
        "windows-update" => WindowsUpdate(),
        "time" => Time(),
        _ => throw new CatalogRequestException($"Unknown repair: {request.Id}"),
    };

    private OperationResult SystemFiles() =>
        runner.Run("sfc.exe", ["/scannow"], Long) == 0
            ? new OperationResult(true, "The check finished. Any problems it could fix are fixed.")
            : new OperationResult(false, "The check couldn't finish. Try Repair Windows image, then run this again.");

    private OperationResult WindowsImage() =>
        runner.Run("dism.exe", ["/Online", "/Cleanup-Image", "/RestoreHealth"], Long) == 0
            ? new OperationResult(true, "The Windows image is healthy.")
            : new OperationResult(false, "The repair couldn't finish. Check your internet connection and try again.");

    private OperationResult Network()
    {
        var failed = 0;
        failed += Failed(runner.Run("ipconfig.exe", ["/flushdns"], Short));
        failed += Failed(runner.Run("netsh.exe", ["winsock", "reset"], Short));

        // netsh exits with 1 here even when every step worked, because it wants a restart.
        var ipReset = runner.Run("netsh.exe", ["int", "ip", "reset"], Short);
        failed += ipReset is 0 or 1 ? 0 : 1;
        return failed == 0
            ? new OperationResult(true, "The network was reset. Restart to finish.", true)
            : new OperationResult(false, "Part of the reset failed. Restart and try again.", true);
    }

    private OperationResult WindowsUpdate()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        foreach (var service in UpdateServices)
        {
            runner.Run("net.exe", ["stop", service, "/y"], Short);
        }

        var moved = true;
        try
        {
            // Renamed rather than deleted, so nothing is lost if this goes wrong.
            folders.Move(Path.Combine(windows, "SoftwareDistribution"), Path.Combine(windows, $"SoftwareDistribution.old-{stamp}"));
            folders.Move(Path.Combine(windows, "System32", "catroot2"), Path.Combine(windows, "System32", $"catroot2.old-{stamp}"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            moved = false;
        }
        finally
        {
            foreach (var service in UpdateServices.Reverse())
            {
                runner.Run("net.exe", ["start", service], Short);
            }
        }

        return moved
            ? new OperationResult(true, "Windows Update was reset.")
            : new OperationResult(false, "A file was in use, so the reset didn't finish. Restart and try again.");
    }

    private OperationResult Time()
    {
        runner.Run("net.exe", ["start", "w32time"], Short);
        return runner.Run("w32tm.exe", ["/resync", "/force"], Short) == 0
            ? new OperationResult(true, "The clock is synced.")
            : new OperationResult(false, "The time server couldn't be reached. Check your internet connection.");
    }

    private static int Failed(int code) => code == 0 ? 0 : 1;
}

public interface IDirectoryMover
{
    void Move(string from, string to);
}

public sealed class WindowsDirectoryMover : IDirectoryMover
{
    public void Move(string from, string to)
    {
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
    }
}
