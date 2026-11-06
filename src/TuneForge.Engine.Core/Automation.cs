using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using TuneForge.Contracts;

namespace TuneForge.Engine.Core;

/// <summary>Saves settings in the person's own folder. A damaged file falls back to the safe defaults.</summary>
public sealed class SettingsStore(string path)
{
    public const int MinThreshold = 50;
    public const int MaxThreshold = 95;
    public const int MinDays = 1;
    public const int MaxDays = 90;

    private readonly Lock _gate = new();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TuneForge",
        "settings.json");

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), RpcProtocol.Json);
                return settings?.Choices is null ? new AppSettings(new SettingsUpdate()) : settings with { Choices = Limit(settings.Choices) };
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                return new AppSettings(new SettingsUpdate());
            }
        }
    }

    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        lock (_gate)
        {
            var next = change(Load());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Written beside the real file first, so a power cut can't leave half a file.
            var partial = path + ".tmp";
            File.WriteAllText(partial, JsonSerializer.Serialize(next, RpcProtocol.Json));
            File.Move(partial, path, overwrite: true);
            return next;
        }
    }

    public AppSettings SaveChoices(SettingsUpdate choices) => Update(settings => settings with
    {
        Choices = Limit(choices),

        // Watching again later starts from what's there then, so old changes don't all alert at once.
        KnownStartup = choices.StartupWatch ? settings.KnownStartup : null,
    });

    public AppSettings SetIgnoredCheck(string id, bool ignored) => Update(settings =>
    {
        var others = (settings.IgnoredChecks ?? []).Where(other => other != id);
        return settings with { IgnoredChecks = (ignored ? others.Append(id) : others).ToList() };
    });

    private static SettingsUpdate Limit(SettingsUpdate choices) => choices with
    {
        MemoryThreshold = Math.Clamp(choices.MemoryThreshold, MinThreshold, MaxThreshold),
        CleanDays = Math.Clamp(choices.CleanDays, MinDays, MaxDays),
    };
}

public interface IAutomationActions
{
    double MemoryPercent();

    MemoryResult TrimMemory();

    /// <summary>Cleans the safe categories that need no admin rights.</summary>
    CleanupResult CleanFiles();

    IReadOnlyList<string> StartupNames();

    OperationResult BackupRegistry();
}

/// <summary>
/// One check of everything that's switched on. It only runs while TuneForge is open or in the tray,
/// because the app has no background service, and it never uses admin rights.
/// </summary>
public sealed class AutomationRunner(
    SettingsStore store,
    IAutomationActions actions,
    Func<DateTimeOffset> now,
    Action<string, string> notify)
{
    private const double Megabyte = 1024d * 1024;
    private static readonly TimeSpan MemoryRest = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan BackupRest = TimeSpan.FromDays(7);

    public void Tick()
    {
        var settings = store.Load();
        if (settings.Choices.MemoryAuto)
        {
            Memory(settings);
        }

        if (settings.Choices.CleanAuto)
        {
            Clean(settings);
        }

        if (settings.Choices.StartupWatch)
        {
            Startup(settings);
        }

        if (settings.Choices.RegistryBackup && Elapsed(settings.LastBackupUtc, BackupRest))
        {
            var result = actions.BackupRegistry();
            store.Update(current => current with { LastBackupUtc = Stamp() });
            if (!result.Ok)
            {
                Tell(settings, "Registry backup failed", result.Message);
            }
        }
    }

    private void Tell(AppSettings settings, string title, string message)
    {
        if (settings.Choices.Notifications)
        {
            notify(title, message);
        }
    }

    private bool Elapsed(string? lastUtc, TimeSpan rest) =>
        !DateTimeOffset.TryParse(lastUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var last)
        || now() - last >= rest;

    private string Stamp() => now().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private void Memory(AppSettings settings)
    {
        if (actions.MemoryPercent() < settings.Choices.MemoryThreshold || !Elapsed(settings.LastMemoryUtc, MemoryRest))
        {
            return;
        }

        var result = actions.TrimMemory();
        store.Update(current => current with { LastMemoryUtc = Stamp() });
        if (!result.Skipped)
        {
            Tell(settings, "Memory cleaned", $"{result.FreedBytes / Megabyte:0} MB freed.");
        }
    }

    private void Clean(AppSettings settings)
    {
        if (settings.LastCleanUtc is null)
        {
            // The first clean waits a full interval, so switching this on deletes nothing straight away.
            store.Update(current => current with { LastCleanUtc = Stamp() });
            return;
        }

        if (!Elapsed(settings.LastCleanUtc, TimeSpan.FromDays(settings.Choices.CleanDays)))
        {
            return;
        }

        var result = actions.CleanFiles();
        store.Update(current => current with { LastCleanUtc = Stamp() });
        if (result.FreedBytes > 0)
        {
            Tell(settings, "Junk files cleaned", $"{result.FreedBytes / Megabyte:0} MB freed.");
        }
    }

    private void Startup(AppSettings settings)
    {
        var names = actions.StartupNames();
        if (settings.KnownStartup is { } known)
        {
            var added = names.Except(known, StringComparer.OrdinalIgnoreCase).ToList();
            if (added.Count == 1)
            {
                Tell(settings, "New startup program", $"{added[0]} now starts with Windows.");
            }
            else if (added.Count > 1)
            {
                Tell(settings, "New startup programs", $"{added.Count} programs now start with Windows: {string.Join(", ", added)}.");
            }

            if (added.Count == 0 && names.Count == known.Count)
            {
                return;
            }
        }

        store.Update(current => current with { KnownStartup = names });
    }
}

/// <summary>Host actions, using the same services as the Cleanup and Startup pages.</summary>
public sealed class AutomationActions(CleanupService cleanup, StartupService startup, ILiveProbe live, IMemoryTrimmer trimmer, RegistryBackup backup) : IAutomationActions
{
    public OperationResult BackupRegistry() => backup.Run(DateTime.Now);

    public double MemoryPercent()
    {
        var (used, total) = live.ReadMemory();
        return total == 0 ? 0 : 100d * used / total;
    }

    public MemoryResult TrimMemory() => trimmer.Trim();

    public CleanupResult CleanFiles() => cleanup.Clean(
        cleanup.Scan().Where(category => category.DefaultOn && !category.NeedsAdmin).Select(category => category.Id),
        elevated: false);

    public IReadOnlyList<string> StartupNames() =>
        startup.List().Where(entry => entry.Enabled).Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public interface IMemoryTrimmer
{
    MemoryResult Trim();
}

/// <summary>
/// Asks programs to hand back memory they aren't actively using. Windows reloads what they need,
/// so this helps only when memory is tight. The active window is left alone, so a game can't stutter.
/// </summary>
public sealed class WindowsMemoryTrimmer(ILiveProbe live) : IMemoryTrimmer
{
    private const uint QueryAndQuota = 0x0400 | 0x0100;
    private const int SettleMs = 500;

    public MemoryResult Trim()
    {
        var (before, total) = live.ReadMemory();
        var running = Process.GetProcesses();
        try
        {
            // Anti-cheat watches for programs opening other processes, so this doesn't run at all near one.
            if (ProtectedProcesses.AnyRunning(running.Select(process => process.ProcessName)))
            {
                return new MemoryResult(before, before, total, 0, Skipped: true);
            }

            _ = GetWindowThreadProcessId(GetForegroundWindow(), out var active);
            var trimmed = running.Count(process => process.Id != active && process.Id != 0 && TrimOne(process.Id));
            Thread.Sleep(SettleMs);
            return new MemoryResult(before, live.ReadMemory().Used, total, trimmed);
        }
        finally
        {
            foreach (var process in running)
            {
                process.Dispose();
            }
        }
    }

    private static bool TrimOne(int id)
    {
        var handle = OpenProcess(QueryAndQuota, false, (uint)id);
        if (handle == 0)
        {
            return false;
        }

        try
        {
            return EmptyWorkingSet(handle);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(nint process);
}

public interface IAutostart
{
    bool Enabled { get; }

    void Set(bool enabled, string executable);
}

/// <summary>Starts TuneForge in the tray at sign-in, for this person only.</summary>
public sealed class WindowsAutostart : IAutostart
{
    public const string TrayArgument = "--tray";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TuneForge";

    public bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
    }

    public void Set(bool enabled, string executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey)
            ?? throw new Win32Exception("The startup setting couldn't be opened.");
        if (enabled)
        {
            key.SetValue(ValueName, Command(executable));
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public static string Command(string executable) => $"\"{executable}\" {TrayArgument}";
}
