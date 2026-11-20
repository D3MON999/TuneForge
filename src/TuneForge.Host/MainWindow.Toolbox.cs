using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TuneForge.Contracts;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Host;

/// <summary>The smaller tools: crashes, network checks, duplicates, leftovers, snapshots and permissions.</summary>
public partial class MainWindow
{
    private static readonly TimeSpan DelayedStartWindow = TimeSpan.FromMinutes(10);
    private static readonly HashSet<string> SettingsPages = new(StringComparer.Ordinal)
    {
        "ms-settings:privacy-webcam", "ms-settings:privacy-microphone", "ms-settings:privacy-location", "ms-settings:appsfeatures",
    };

    private readonly HealthHistory _history = new(HealthHistory.DefaultPath);
    private readonly SnapshotStore _snapshots = new(SnapshotStore.DefaultFolder);
    private readonly ConnectionLogger _connectionLog = new(
        new WindowsPinger(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TuneForge", "connection-log.csv"),
        () => DateTimeOffset.Now);

    private CancellationTokenSource? _duplicateScan;

    private async Task<object?> RunToolboxAsync(string op, JsonElement payload) => op switch
    {
        "crash.list" => await Task.Run(() => CrashReader.Read(new WindowsEventSource())),
        "restorepoints.list" => await _engine.ListRestorePointsAsync(),
        "restorepoints.delete" => await _engine.DeleteRestorePointAsync(Read<RestorePointRequest>(payload).Sequence),
        "iconcache.rebuild" => await Task.Run(IconCache.Rebuild),
        "link.read" => await Task.Run(() => LinkSpeed.Read(null)),
        "wifi.scan" => await Task.Run(WifiReader.Scan),
        "connlog.status" => _connectionLog.Status(),
        "connlog.set" => SetConnectionLog(Read<ConnectionLogRequest>(payload).Running),
        "connlog.open" => OpenConnectionLog(),
        "hosts.read" => await Task.Run(() => new HostsFile(HostsFile.DefaultPath, HostsBackupFolder).Read()),
        "hosts.remove" => await _engine.RemoveHostsLinesAsync(Read<HostsRemoveRequest>(payload).Lines),
        "hosts.restore" => await _engine.RestoreHostsAsync(),
        "disk.speed" => await Task.Run(() => DiskSpeed.Run(Read<DiskSpeedRequest>(payload).Drive)),
        "duplicates.scan" => await ScanDuplicatesAsync(Read<DuplicateRequest>(payload).Where),
        "duplicates.recycle" => await Task.Run(() => RecycleDuplicates(Read<DuplicateRecycleRequest>(payload).Picks)),
        "leftovers.find" => await Task.Run(() => FindLeftovers(Read<LeftoverRequest>(payload).Name)),
        "leftovers.recycle" => await Task.Run(() => RecycleLeftovers(Read<LeftoverRecycleRequest>(payload))),
        "startup.delayed" => ReadSettings().Delayed ?? [],
        "startup.delay" => await DelayStartupAsync(Read<StartupDelayRequest>(payload)),
        "background.read" => await Task.Run(BackgroundApps.Read),
        "background.set" => await Task.Run(() => BackgroundApps.Set(Read<BackgroundAppRequest>(payload))),
        "health.history" => _history.Read(),
        "changes.read" => await Task.Run(ReadChanges),
        "permissions.read" => await Task.Run(PermissionReader.Read),
        "settings.page" => OpenSettingsPage(Read<SettingsPageRequest>(payload).Page),
        _ => throw new EngineException("Unknown operation."),
    };

    private static string HostsBackupFolder => Path.Combine(JournalLocation.Folder, "hosts-backup");

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Every check also goes into the history, and the day's snapshot is taken if it hasn't been yet.</summary>
    private HealthReport CheckHealth()
    {
        var report = _health.Check();
        _history.Record(report.Score, Today);
        if (!_snapshots.HasToday(Today))
        {
            _ = Task.Run(TakeSnapshot);
        }

        return report;
    }

    private void TakeSnapshot()
    {
        try
        {
            _snapshots.Save(SnapshotStore.Take(_startup, Today), Today);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Tried again at the next health check.
        }
    }

    private ChangesReport ReadChanges()
    {
        var current = SnapshotStore.Take(_startup, Today);
        var report = _snapshots.Compare(current, Today);
        if (!_snapshots.HasToday(Today))
        {
            _snapshots.Save(current, Today);
        }

        return report;
    }

    private void SyncConnectionLog(bool on)
    {
        if (on)
        {
            _connectionLog.Start(LinkSpeed.Router());
        }
        else
        {
            _connectionLog.Stop();
        }
    }

    private ConnectionLogStatus SetConnectionLog(bool running)
    {
        // Saved, so the log carries on the next time TuneForge starts.
        _settings.Update(settings => settings with { Choices = settings.Choices with { ConnectionLog = running } });
        SyncConnectionLog(running);
        return _connectionLog.Status();
    }

    private bool OpenConnectionLog()
    {
        if (!File.Exists(_connectionLog.LogPath))
        {
            throw new CatalogRequestException("Nothing has been logged yet.");
        }

        return ShowInExplorer(_connectionLog.LogPath);
    }

    private async Task<DuplicateReport?> ScanDuplicatesAsync(string where)
    {
        var scan = new CancellationTokenSource();
        Interlocked.Exchange(ref _duplicateScan, scan)?.Cancel();
        var roots = DuplicateFinder.Roots(where, DownloadsFolder());
        try
        {
            return await Task.Run(() => DuplicateFinder.Scan(roots, scan.Token), scan.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Only files in your own folders or on other drives, and only while an identical copy stays.</summary>
    private static RecycleResult RecycleDuplicates(IReadOnlyList<DuplicatePick> picks)
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var roots = DuplicateFinder.PersonalFolders(DownloadsFolder())
            .Concat(DriveInfo.GetDrives()
                .Where(drive => drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable && !drive.Name.Equals(system, StringComparison.OrdinalIgnoreCase))
                .Select(drive => drive.Name))
            .ToList();
        return DuplicateFinder.Recycle(picks, roots, RecycleOne);
    }

    private static IEnumerable<(string Root, bool Removable)> LeftoverRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return LeftoverFinder.DefaultRoots(new UserPaths(profile));
    }

    private static IReadOnlyList<LeftoverFolder> FindLeftovers(string name) =>
        LeftoverFinder.Find(name, LeftoverRoots(), LeftoverFinder.FolderSize);

    /// <summary>Found again here, so only folders that really match the program, in your own profile, can go.</summary>
    private static RecycleResult RecycleLeftovers(LeftoverRecycleRequest request)
    {
        var allowed = LeftoverFinder.Find(request.Name, LeftoverRoots(), _ => 0)
            .Where(folder => folder.CanRemove)
            .Select(folder => folder.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int moved = 0, failed = 0;
        foreach (var path in request.Paths)
        {
            if (allowed.Contains(path) && RecycleOne(path))
            {
                moved++;
            }
            else
            {
                failed++;
            }
        }

        return new RecycleResult(moved, failed);
    }

    /// <summary>
    /// A delayed program is switched off in Windows' startup list, and TuneForge starts it a minute after sign-in.
    /// That only works while TuneForge starts with Windows, so it asks for that first.
    /// </summary>
    private async Task<OperationResult> DelayStartupAsync(StartupDelayRequest request)
    {
        var settings = ReadSettings();
        var delayed = (settings.Delayed ?? []).ToList();
        if (request.Delayed)
        {
            if (!settings.Choices.StartWithWindows)
            {
                return new OperationResult(false, "Turn on Start with Windows in Settings first. TuneForge starts delayed programs itself.");
            }

            var entry = _startup.List().FirstOrDefault(item => item.Source == request.Source && item.Name == request.Name)
                ?? throw new CatalogRequestException("That startup program no longer exists.");
            if (entry.Source.EndsWith("folder", StringComparison.Ordinal) ? !File.Exists(entry.Command) : StartupLaunch.Split(entry.Command) is null)
            {
                return new OperationResult(false, "TuneForge can't tell how to start this program, so it can't be delayed.");
            }

            var result = await RefreshAfter(_engine.SetStartupAsync(new StartupSetRequest(request.Source, request.Name, false)));
            if (!result.Ok)
            {
                return result;
            }

            delayed.RemoveAll(item => item.Source == request.Source && item.Name == request.Name);
            delayed.Add(new DelayedStartup(entry.Source, entry.Name, entry.Command, 60));
            _settings.Update(current => current with { Delayed = delayed });
            return new OperationResult(true, $"{entry.Name} now starts a minute after you sign in.");
        }

        var back = await RefreshAfter(_engine.SetStartupAsync(new StartupSetRequest(request.Source, request.Name, true)));
        if (back.Ok)
        {
            delayed.RemoveAll(item => item.Source == request.Source && item.Name == request.Name);
            _settings.Update(current => current with { Delayed = delayed });
            return new OperationResult(true, $"{request.Name} starts with Windows again.");
        }

        return back;
    }

    /// <summary>Runs when TuneForge itself started with Windows. A later restart of TuneForge doesn't start them again.</summary>
    public void StartDelayedPrograms()
    {
        var delayed = ReadSettings().Delayed ?? [];
        if (delayed.Count == 0 || TimeSpan.FromMilliseconds(Environment.TickCount64) > DelayedStartWindow)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var waited = 0;
            foreach (var group in delayed.GroupBy(item => item.DelaySeconds).OrderBy(group => group.Key))
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, group.Key - waited)));
                waited = group.Key;
                foreach (var item in group)
                {
                    if (item.Source.EndsWith("folder", StringComparison.Ordinal))
                    {
                        try
                        {
                            using var _ = Process.Start(new ProcessStartInfo(item.Command) { UseShellExecute = true });
                        }
                        catch (System.ComponentModel.Win32Exception)
                        {
                            // Removed since. Nothing to start.
                        }
                    }
                    else
                    {
                        StartupLaunch.Start(item.Command);
                    }
                }
            }
        });
    }

    private static bool OpenSettingsPage(string page)
    {
        if (!SettingsPages.Contains(page))
        {
            throw new CatalogRequestException("Unknown settings page.");
        }

        using var _ = Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
        return true;
    }
}
