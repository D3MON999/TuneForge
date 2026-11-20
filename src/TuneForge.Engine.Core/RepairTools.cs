using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

/// <summary>
/// Turns Windows' crash records into plain words. The stop code is the same in every language,
/// so it's read from the message rather than the wording around it.
/// </summary>
public static partial class CrashReader
{
    private static readonly TimeSpan SameCrash = TimeSpan.FromMinutes(10);

    private static readonly Dictionary<uint, (string Name, string Meaning)> Codes = new()
    {
        [0x0A] = ("IRQL NOT LESS OR EQUAL", "A driver used memory it isn't allowed to. Usually a faulty or outdated driver, sometimes bad RAM."),
        [0x19] = ("BAD POOL HEADER", "A driver damaged Windows' memory. Update the drivers you changed most recently."),
        [0x1A] = ("MEMORY MANAGEMENT", "A memory problem. Often faulty RAM or an unstable RAM overclock (XMP or EXPO). Try Windows Memory Diagnostic."),
        [0x1E] = ("KMODE EXCEPTION NOT HANDLED", "A driver crashed. Update or roll back drivers that changed recently."),
        [0x24] = ("NTFS FILE SYSTEM", "A file system problem. Run Check the Windows drive on this page."),
        [0x3B] = ("SYSTEM SERVICE EXCEPTION", "A driver or system file crashed. Graphics and antivirus drivers are common causes."),
        [0x50] = ("PAGE FAULT IN NONPAGED AREA", "A bad memory access, from a faulty driver, RAM or a damaged disk."),
        [0x7A] = ("KERNEL DATA INPAGE ERROR", "Windows couldn't read data from the disk. Check the drive's health and its cable."),
        [0x77] = ("KERNEL STACK INPAGE ERROR", "Windows couldn't read data from the disk. Check the drive's health and its cable."),
        [0x7E] = ("SYSTEM THREAD EXCEPTION NOT HANDLED", "A driver crashed, often the graphics driver."),
        [0x7F] = ("UNEXPECTED KERNEL MODE TRAP", "A hardware fault. Usually RAM, overheating or an overclock."),
        [0x9C] = ("MACHINE CHECK EXCEPTION", "The processor reported a fatal error. Overheating or an overclock is the usual cause."),
        [0x9F] = ("DRIVER POWER STATE FAILURE", "A driver didn't handle sleep or wake properly. Update chipset, network and graphics drivers."),
        [0xC2] = ("BAD POOL CALLER", "A driver misused memory. Update the drivers you changed most recently."),
        [0xC5] = ("DRIVER CORRUPTED EXPOOL", "A driver damaged memory. Update the drivers you changed most recently."),
        [0xD1] = ("DRIVER IRQL NOT LESS OR EQUAL", "A driver used memory it isn't allowed to. Network and graphics drivers are common causes."),
        [0xEF] = ("CRITICAL PROCESS DIED", "An essential part of Windows stopped. Run Check system files on this page."),
        [0xF4] = ("CRITICAL OBJECT TERMINATION", "An essential part of Windows stopped, often because the disk couldn't be read."),
        [0x101] = ("CLOCK WATCHDOG TIMEOUT", "A processor core stopped responding. Often an unstable CPU overclock or undervolt."),
        [0x109] = ("CRITICAL STRUCTURE CORRUPTION", "Windows' core was damaged. Often bad RAM or a faulty driver."),
        [0x113] = ("VIDEO DXGKRNL FATAL ERROR", "The graphics system failed. Reinstall the graphics driver."),
        [0x116] = ("VIDEO TDR FAILURE", "The graphics card stopped responding. The graphics driver, overheating or a GPU overclock."),
        [0x119] = ("VIDEO SCHEDULER INTERNAL ERROR", "The graphics system failed. Reinstall the graphics driver."),
        [0x124] = ("WHEA UNCORRECTABLE ERROR", "The hardware reported a fatal error. Usually CPU or RAM instability, an overclock or overheating."),
        [0x133] = ("DPC WATCHDOG VIOLATION", "A driver took too long. Often SSD firmware or an old storage driver."),
        [0x139] = ("KERNEL SECURITY CHECK FAILURE", "Windows caught damaged data. Usually a driver, sometimes RAM."),
        [0x13A] = ("KERNEL MODE HEAP CORRUPTION", "A driver damaged memory. The graphics driver is a common cause."),
        [0x144] = ("BUGCODE USB3 DRIVER", "A USB device or its driver failed. Try other USB ports and update the chipset driver."),
        [0x154] = ("UNEXPECTED STORE EXCEPTION", "A problem reading memory or the disk. Check your drive's health."),
    };

    [GeneratedRegex(@"0x([0-9a-fA-F]{8})")]
    private static partial Regex StopCode();

    [GeneratedRegex(@"[A-Za-z]:\\[^\s""]+?\.dmp", RegexOptions.IgnoreCase)]
    private static partial Regex DumpFile();

    public static (string Name, string Meaning) Describe(uint code) =>
        Codes.TryGetValue(code, out var known)
            ? known
            : ("Stop code", "Windows stopped to protect itself. Search for the code, or look at what changed around this time.");

    /// <summary>Newest first. A sudden restart logged next to a blue screen is the same crash, so it counts once.</summary>
    public static IReadOnlyList<CrashRecord> Summarize(IEnumerable<WindowsEvent> events)
    {
        var crashes = events.Where(EventService.IsCrash)
            .Select(item => (Event: item, When: DateTimeOffset.Parse(item.TimeUtc, CultureInfo.InvariantCulture)))
            .OrderByDescending(item => item.When)
            .ToList();

        var screens = crashes.Where(item => item.Event.EventId == 1001).ToList();
        var records = new List<(DateTimeOffset When, CrashRecord Record)>();
        foreach (var (item, when) in screens)
        {
            var match = StopCode().Match(item.Message);
            uint? code = match.Success ? uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;
            var (name, meaning) = code is { } value ? Describe(value) : ("Blue screen", "Windows stopped to protect itself.");
            records.Add((when, new CrashRecord(
                item.TimeUtc,
                "bluescreen",
                code is { } shown ? $"0x{shown:X8}" : null,
                name,
                meaning,
                DumpFile().Match(item.Message) is { Success: true } dump ? dump.Value : null)));
        }

        foreach (var (item, when) in crashes.Where(item => item.Event.EventId == 41))
        {
            if (screens.Any(screen => (screen.When - when).Duration() <= SameCrash))
            {
                continue;
            }

            records.Add((when, new CrashRecord(
                item.TimeUtc,
                "power-loss",
                null,
                "Sudden restart",
                "Windows didn't shut down properly. The PC lost power, froze and was reset, or was switched off at the button. Repeated ones point to the power supply, overheating or an unstable overclock.",
                null)));
        }

        return records.OrderByDescending(record => record.When).Select(record => record.Record).ToList();
    }

    public static IReadOnlyList<CrashRecord> Read(IEventSource source) => Summarize(source.Read("System", "errors", 90, 1000));
}

/// <summary>Lists and deletes restore points through System Restore itself. Needs admin rights, so it runs in the engine.</summary>
public sealed class RestorePointManager
{
    public RestorePointReport List()
    {
        var points = new List<SavedRestorePoint>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\default"),
                new ObjectQuery("SELECT SequenceNumber, Description, CreationTime FROM SystemRestore"));
            foreach (var item in searcher.Get().Cast<ManagementObject>())
            {
                using (item)
                {
                    var created = item["CreationTime"] is string raw && raw.Length > 0
                        ? ManagementDateTimeConverter.ToDateTime(raw).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                        : string.Empty;
                    points.Add(new SavedRestorePoint(Convert.ToInt64(item["SequenceNumber"], CultureInfo.InvariantCulture), item["Description"] as string ?? "Restore point", created));
                }
            }
        }
        catch (ManagementException)
        {
            // System Restore is off, so there are no points to list.
        }

        long used = 0, max = 0;
        try
        {
            using var storage = new ManagementObjectSearcher("SELECT UsedSpace, MaxSpace FROM Win32_ShadowStorage");
            foreach (var item in storage.Get().Cast<ManagementObject>())
            {
                using (item)
                {
                    used += Convert.ToInt64(item["UsedSpace"], CultureInfo.InvariantCulture);
                    max += Convert.ToInt64(item["MaxSpace"], CultureInfo.InvariantCulture);
                }
            }
        }
        catch (ManagementException)
        {
            // Sizes are a nice extra. The list still works without them.
        }

        return new RestorePointReport(points.OrderByDescending(point => point.CreatedUtc, StringComparer.Ordinal).ToList(), used, max);
    }

    public OperationResult Delete(long sequence)
    {
        if (List().Points.All(point => point.Sequence != sequence))
        {
            throw new CatalogRequestException("That restore point no longer exists.");
        }

        return SRRemoveRestorePoint((uint)sequence) == 0
            ? new OperationResult(true, "The restore point was deleted.")
            : new OperationResult(false, "Windows couldn't delete that restore point.");
    }

    [DllImport("srclient.dll")]
    private static extern int SRRemoveRestorePoint(uint sequence);
}

/// <summary>
/// Rebuilds Explorer's icon and thumbnail caches. Explorer has to close for a moment, because it keeps them open.
/// Only your own cache files are deleted, and Windows makes new ones straight away.
/// </summary>
public static class IconCache
{
    public static IEnumerable<string> CacheFiles(string localAppData)
    {
        var single = Path.Combine(localAppData, "IconCache.db");
        if (File.Exists(single))
        {
            yield return single;
        }

        var explorer = Path.Combine(localAppData, "Microsoft", "Windows", "Explorer");
        if (!Directory.Exists(explorer))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(explorer, "*.db"))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("iconcache_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    public static OperationResult Rebuild()
    {
        var running = Process.GetProcesses();
        try
        {
            if (ProtectedProcesses.AnyGameRunning(running.Select(process => process.ProcessName)))
            {
                return new OperationResult(false, "Close your game first. The taskbar disappears for a few seconds while this runs.");
            }
        }
        finally
        {
            foreach (var process in running)
            {
                process.Dispose();
            }
        }

        var session = Process.GetCurrentProcess().SessionId;
        var removed = 0;
        try
        {
            foreach (var explorer in Process.GetProcessesByName("explorer"))
            {
                using (explorer)
                {
                    if (explorer.SessionId == session)
                    {
                        explorer.Kill();
                        explorer.WaitForExit(5000);
                    }
                }
            }

            foreach (var file in CacheFiles(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Still in use by another program. Windows refreshes it later anyway.
                }
            }
        }
        finally
        {
            using var _ = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true });
        }

        return new OperationResult(true, removed > 0
            ? "Icons and thumbnails are rebuilding. They may look blank for a moment."
            : "There was nothing to clear. Explorer was restarted.");
    }
}
