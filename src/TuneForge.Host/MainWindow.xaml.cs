using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using TuneForge.Contracts;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Host;

public partial class MainWindow : Window
{
    private const string AppHost = "app.tuneforge.local";
    private static readonly string AppOrigin = $"https://{AppHost}";

    private readonly EngineClient _engine = new();
    private readonly TweakScanner _scanner;
    private readonly CleanupService _cleanup;
    private readonly StartupService _startup;
    private readonly ServiceManager _services;
    private readonly FeatureService _features;
    private readonly BuiltInAppService _builtIn;
    private readonly WingetClient _winget;
    private readonly WindowsProcessRunner _runner = new();
    private readonly LiveMonitor _live;
    private readonly HealthService _health;
    private readonly IStorageReader _storage = new WindowsStorageReader();
    private readonly EventService _events = new(new WindowsEventSource());
    private readonly NetworkTools _network = new(new WindowsNetworkProbe());
    private readonly WindowsDnsAdapters _dns = new();
    private readonly SettingsStore _settings = new(SettingsStore.DefaultPath);
    private readonly WindowsAutostart _autostart = new();
    private readonly WindowsMemoryTrimmer _trimmer;
    private readonly TrayAgent _tray;
    private readonly WindowsPowerPlans _plans;
    private readonly GameScanner _gameScanner;
    private readonly WindowsRunningApps _apps = new();
    private readonly ProcessService _processes = new();
    private readonly WindowsDisplayModes _display = new();
    private readonly CloudflareSpeedProbe _speed = new();
    private readonly ConnectionScanner _connections = new(new WindowsProcessRunner());
    private readonly IReadOnlyList<string> _softwareIds = AppCatalogs.LoadSoftware().Select(item => item.Id).ToList();
    private readonly AdvancedService _advanced = new(
        new WindowsProcessRunner(),
        new FileAdvancedState(JournalLocation.AdvancedStatePath),
        new WindowsAdapterSettings());
    private readonly UpdateService _updates = new(
        new GitHubReleaseFeed(),
        UpdateService.CurrentVersion,
        UpdateService.LoadPublicKey(),
        UpdateService.DefaultFolder);
    private volatile IReadOnlyList<GameEntry> _games = [];
    private static readonly TimeSpan UpdateCheckEvery = TimeSpan.FromHours(6);
    private static readonly TimeSpan BusyRetry = TimeSpan.FromMinutes(10);
    private readonly System.Windows.Threading.DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(2) };
    private volatile UpdateStatus? _pendingUpdate;
    private CancellationTokenSource? _spaceScan;
    private int _updateNudge;
    private bool _exiting;
    private WindowState _stateBeforeFullScreen;
    private WindowStyle _styleBeforeFullScreen;

    public MainWindow()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var registry = new WindowsRegistryStore(identity.User!);
        var paths = UserPaths.For(identity.User!);
        _scanner = new TweakScanner(TweakCatalog.LoadEmbedded(), registry, Environment.OSVersion.Version.Build);
        _cleanup = new CleanupService(CleanupService.Standard(paths), new WindowsRecycleBin());
        _startup = new StartupService(registry, paths);
        _services = new ServiceManager(registry, new WindowsServiceSource());
        _features = new FeatureService(_runner, new WmiFeatureStateReader());
        _builtIn = new BuiltInAppService(_runner, AppCatalogs.LoadBuiltIn());
        _winget = new WingetClient(_runner, AppCatalogs.LoadSoftware());
        var probe = new WindowsLiveProbe(_runner);
        _live = new LiveMonitor(probe);
        _health = new HealthService(_storage, _events, _startup, _cleanup, probe, _settings);
        _trimmer = new WindowsMemoryTrimmer(probe);
        _plans = new WindowsPowerPlans(_runner);
        _gameScanner = new GameScanner(GameSources.Detect());
        _tray = new TrayAgent(
            _settings,
            new AutomationActions(_cleanup, _startup, probe, _trimmer, new RegistryBackup(_runner, RegistryBackup.DefaultFolder)),
            new WindowsGameSettings(),
            _plans,
            _apps,
            ShowFromTray,
            ExitApp);
        _tray.Apply(ReadSettings().Choices);

        InitializeComponent();
        Web.DefaultBackgroundColor = System.Drawing.Color.Black;
        _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
        _updateTimer.Start();
        _ = Task.Run(ClearOldUpdates);

        // Black until the page loads and says which theme is on, so the bar doesn't flash white.
        SourceInitialized += (_, _) => SetTitleBar(new WindowThemeRequest("#000000", true));
        Loaded += async (_, _) => await StartWebAsync();
        Closing += (_, e) =>
        {
            if (!_exiting && ReadSettings().Choices.CloseToTray)
            {
                e.Cancel = true;
                Hide();
            }
        };
        Closed += (_, _) => ExitApp();
    }

    /// <summary>A new start means any update has finished, so the downloaded installer is no longer needed.</summary>
    private static void ClearOldUpdates()
    {
        try
        {
            if (!Directory.Exists(UpdateService.DefaultFolder))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(UpdateService.DefaultFolder))
            {
                File.Delete(file);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // It's only disk space. The next download clears the folder anyway.
        }
    }

    /// <summary>
    /// Runs while the app is open or in the tray. It only ever asks, and nothing installs without a click.
    /// An important update brings the window up, but never over a game: it tries again shortly after.
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        _updateTimer.Interval = UpdateCheckEvery;
        try
        {
            var status = await Task.Run(_updates.Check);
            _pendingUpdate = status.Available ? status : null;
            if (status is not { Available: true, Important: true })
            {
                return;
            }

            if (await Task.Run(UserBusy.Now))
            {
                _updateTimer.Interval = BusyRetry;
                return;
            }

            _updateNudge++;
            ShowFromTray();
        }
        catch (Exception exception)
        {
            // This runs from a timer, so anything left uncaught would close the app.
            App.LogCrash(exception);
        }
    }

    private bool SetTitleBar(WindowThemeRequest request)
    {
        TitleBar.Apply(new WindowInteropHelper(this).EnsureHandle(), request.Color, request.Dark);
        return true;
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void ExitApp()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        Close();
        _tray.Dispose();
        _engine.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>The Windows setting wins, because it can be changed outside the app.</summary>
    private AppSettings ReadSettings()
    {
        var settings = _settings.Load();
        return settings with { Choices = settings.Choices with { StartWithWindows = _autostart.Enabled } };
    }

    private AppSettings SaveSettings(SettingsUpdate choices)
    {
        _autostart.Set(choices.StartWithWindows, Environment.ProcessPath!);
        _settings.SaveChoices(choices);
        var saved = ReadSettings();
        _tray.Apply(saved.Choices);
        return saved;
    }

    private async Task StartWebAsync()
    {
        var dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TuneForge",
            "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
        await Web.EnsureCoreWebView2Async(environment);

        var core = Web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        core.SetVirtualHostNameToFolderMapping(
            AppHost,
            Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            CoreWebView2HostResourceAccessKind.Deny);
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(AppOrigin + "/", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += OnWebMessage;
        core.ContainsFullScreenElementChanged += (_, _) => SetFullScreen(core.ContainsFullScreenElement);

        // The camera and microphone tests are the only things that ask, and only after a click.
        core.PermissionRequested += (_, e) =>
        {
            var allowed = e.Uri.StartsWith(AppOrigin + "/", StringComparison.OrdinalIgnoreCase)
                && e.PermissionKind is CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone;
            e.State = allowed ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        };
        core.Navigate($"{AppOrigin}/index.html");
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(AppOrigin + "/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string id;
        string op;
        JsonElement payload;
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            id = message.RootElement.GetProperty("id").GetString() ?? string.Empty;
            op = message.RootElement.GetProperty("op").GetString() ?? string.Empty;
            payload = message.RootElement.TryGetProperty("payload", out var value) ? value.Clone() : default;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return;
        }

        object reply;
        try
        {
            reply = new { id, ok = true, result = await RunAsync(op, payload) };
        }
        catch (EngineException exception)
        {
            reply = new { id, ok = false, error = exception.Message };
        }
        catch (CatalogRequestException exception)
        {
            reply = new { id, ok = false, error = exception.Message };
        }
        catch (Exception exception) when (exception is JsonException)
        {
            reply = new { id, ok = false, error = "Invalid request." };
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            reply = new { id, ok = false, error = "The engine could not be reached." };
        }
        catch (Exception exception)
        {
            // This handler is async void, so anything left uncaught here would close the whole app.
            App.LogCrash(exception);
            reply = new { id, ok = false, error = "Something went wrong reading that. Try again." };
        }

        if (_exiting)
        {
            return;
        }

        Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(reply, RpcProtocol.Json));
    }

    /// <summary>The complete list of what the interface may ask for.</summary>
    private async Task<object?> RunAsync(string op, JsonElement payload) => op switch
    {
        "engine.connect" => await _engine.PingAsync(),
        "tweaks.scan" => await Task.Run(ScanTweaks),
        "tweaks.preview" => await Task.Run(() => _scanner.Preview(Read<PreviewPayload>(payload).Ids)),
        "tweaks.apply" => await RefreshAfter(_engine.ApplyAsync(Read<ApplyRequest>(payload))),
        "tweaks.removeLocks" => await RefreshAfter(_engine.RemoveLocksAsync(Read<RemoveLocksRequest>(payload).Ids)),
        "history.list" => await Task.Run(ReadHistory),
        "history.undo" => await RefreshAfter(_engine.UndoAsync(Read<UndoRequest>(payload).ChangeSetId)),
        "history.undoAll" => await RefreshAfter(_engine.UndoAllAsync()),
        "space.scan" => await ScanSpaceAsync(Read<SpaceRequest>(payload).Path),
        "space.show" => ShowInExplorer(Read<SpaceRequest>(payload).Path),
        "driver.read" => await Task.Run(GraphicsDrivers.Read),
        "driver.page" => OpenDriverPage(Read<DriverPageRequest>(payload).Vendor),
        "driver.packages" => await Task.Run(() => new DriverCleanState(
            DisplayDriverRemover.Scan(DisplayDriverRemover.DefaultInfFolder),
            DisplayDriverRemover.Backups(DisplayDriverRemover.DefaultBackupRoot))),
        "driver.remove" => await _engine.RemoveDriverAsync(Read<DriverPageRequest>(payload).Vendor),
        "driver.restore" => await _engine.RestoreDriverAsync(Read<DriverPageRequest>(payload).Vendor),
        "driver.deleteBackup" => await _engine.DeleteDriverBackupAsync(Read<DriverPageRequest>(payload).Vendor),
        "boot.times" => await ReadBootTimesAsync(),
        "battery.read" => await Task.Run(() => BatteryHealth.Read(_runner)),
        "downloads.old" => await Task.Run(() => OldDownloads.Scan(DownloadsFolder(), DateTime.UtcNow)),
        "downloads.recycle" => await Task.Run(() => RecycleDownloads(Read<RecycleRequest>(payload).Paths)),
        "cleanup.scan" => await Task.Run(_cleanup.Scan),
        "cleanup.run" => await CleanAsync(Read<CleanupRequest>(payload).Ids),
        "startup.list" => await Task.Run(_startup.List),
        "startup.set" => await RefreshAfter(_engine.SetStartupAsync(Read<StartupSetRequest>(payload))),
        "service.list" => await Task.Run(_services.List),
        "service.set" => await _engine.SetServiceAsync(Read<ServiceSetRequest>(payload)),
        "feature.list" => await Task.Run(_features.List),
        "feature.set" => await _engine.SetFeatureAsync(Read<FeatureSetRequest>(payload)),
        "repair.list" => RepairService.Available,
        "repair.run" => await _engine.RepairAsync(Read<RepairRequest>(payload)),
        "builtin.list" => await Task.Run(_builtIn.List),
        "builtin.remove" => await _engine.RemoveBuiltInAsync(Read<BuiltInRemoveRequest>(payload)),
        "builtin.reinstall" => await Task.Run(() => _winget.Reinstall(_builtIn.Find(Read<AppIdRequest>(payload).Id))),
        "software.list" => await Task.Run(_winget.ListSoftware),
        "software.install" => await Task.Run(() => _winget.Install(Read<AppIdRequest>(payload).Id)),
        "programs.list" => await Task.Run(_winget.ListInstalled),
        "programs.update" => await Task.Run(() => _winget.Update(Read<AppIdRequest>(payload).Id)),
        "programs.uninstall" => await Task.Run(() => _winget.Uninstall(Read<AppIdRequest>(payload).Id)),
        "system.summary" => await Task.Run(SystemInfoReader.Read),
        "monitor.live" => await Task.Run(_live.Sample),
        "health.check" => await Task.Run(_health.Check),
        "health.ignore" => await Task.Run(() => _health.SetIgnored(Read<HealthIgnoreRequest>(payload))),
        "window.theme" => SetTitleBar(Read<WindowThemeRequest>(payload)),
        "storage.read" => await Task.Run(_storage.Read),
        "events.errors" => await Task.Run(() => EventService.Group(_events.RecentErrors())),
        "events.read" => await Task.Run(() => _events.Read(Read<EventLogRequest>(payload))),
        "report.save" => await SaveReportAsync(),
        "network.ping" => await Task.Run(() => _network.Ping(Read<ProbeRequest>(payload))),
        "network.tcping" => await Task.Run(() => _network.TcpPing(Read<ProbeRequest>(payload))),
        "network.trace" => await Task.Run(() => _network.Trace(Read<ProbeRequest>(payload))),
        "dns.status" => await Task.Run(ReadDnsStatus),
        "dns.benchmark" => await Task.Run(() => _network.BenchmarkDns(_dns.ReadCurrent())),
        "dns.set" => await _engine.SetDnsAsync(Read<DnsSetRequest>(payload)),
        "dns.flush" => await Task.Run(FlushDns),
        "settings.get" => ReadSettings(),
        "settings.save" => SaveSettings(Read<SettingsUpdate>(payload)),
        "memory.clean" => await Task.Run(_trimmer.Trim),
        "games.list" => await Task.Run(ScanGames),
        "games.save" => await Task.Run(() => SaveGame(Read<GameProfileRequest>(payload))),
        "gaming.overview" => await Task.Run(ReadGamingOverview),
        "power.set" => await Task.Run(() => SetPlan(Read<PowerPlanRequest>(payload).Id)),
        "power.ultimate" => await Task.Run(AddUltimatePlan),
        "focus.save" => await Task.Run(() => SaveFocusApps(Read<FocusAppsRequest>(payload).Names)),
        "advanced.list" => await Task.Run(_advanced.List),
        "advanced.set" => await _engine.SetAdvancedAsync(Read<AdvancedSetRequest>(payload)),
        "security.read" => await Task.Run(new WindowsSecurityReader().Read),
        "security.set" => await _engine.SetSecurityAsync(Read<SecuritySetRequest>(payload)),
        "tools.list" => WindowsToolLinks.All.Select(tool => tool.Link).ToList(),
        "tools.open" => OpenTool(Read<ToolOpenRequest>(payload).Id),
        "process.list" => await Task.Run(_processes.List),
        "process.end" => await Task.Run(() => _processes.End(Read<ProcessEndRequest>(payload))),
        "winget.repair" => await Task.Run(_builtIn.RepairWinget),
        "power.create" => await Task.Run(() => new PowerTools(_runner).CreatePlan(Read<ProbeRequest>(payload).Host)),
        "power.parking" => await Task.Run(new PowerTools(_runner).ReadCoreParking),
        "power.keepCores" => await Task.Run(new PowerTools(_runner).KeepCoresAwake),
        "display.read" => await Task.Run(_display.Read),
        "display.best" => await Task.Run(SetBestDisplay),
        "connections.scan" => await Task.Run(ScanConnections),
        "connections.history" => _connections.History().Select(item => new ActivityItem(item.Key, item.Value)).ToList(),
        "settings.export" => await ExportSettingsAsync(),
        "settings.import" => await ImportSettingsAsync(),
        "speed.run" => await Task.Run(new SpeedTest(_speed).Run),
        "bandwidth.list" => await Task.Run(new BandwidthLimits(_runner).List),
        "bandwidth.set" => await _engine.SetBandwidthAsync(Read<BandwidthRule>(payload)),
        "bandwidth.remove" => await _engine.RemoveBandwidthAsync(Read<BandwidthRule>(payload)),
        "gpu.health" => await Task.Run(() => GpuChecks.Read(_runner)),
        "bios.restart" => await _engine.RestartToBiosAsync(),
        "bios.cancel" => await _engine.CancelBiosRestartAsync(),
        "registry.backup" => await Task.Run(() => new RegistryBackup(_runner, RegistryBackup.DefaultFolder).Run(DateTime.Now)),
        "turbo.get" => _tray.Games.TurboOn,
        "turbo.set" => await Task.Run(() => SetTurbo(Read<TurboRequest>(payload).Enabled)),
        "games.verify" => VerifySteamGame(Read<SteamVerifyRequest>(payload).GameId),
        "crash.open" => OpenCrashFolder(),
        "update.check" => await Task.Run(_updates.Check),
        "update.pending" => _pendingUpdate is { } pending ? new UpdatePrompt(pending, _updateNudge) : null,
        "update.install" => await InstallUpdateAsync(),
        _ => throw new EngineException("Unknown operation."),
    };

    /// <summary>
    /// Your own files are cleaned without admin rights, so the elevated engine never walks folders
    /// that other programs can write to. A declined prompt stops before anything is deleted.
    /// </summary>
    private async Task<CleanupResult> CleanAsync(IReadOnlyList<string> ids)
    {
        var admin = ids.Where(_cleanup.NeedsAdmin).ToList();
        var fromEngine = admin.Count > 0 ? await _engine.CleanAsync(admin) : new CleanupResult(0, 0, 0);
        var mine = await Task.Run(() => _cleanup.Clean(ids.Except(admin), elevated: false));
        return new CleanupResult(
            fromEngine.FreedBytes + mine.FreedBytes,
            fromEngine.Files + mine.Files,
            fromEngine.Skipped + mine.Skipped);
    }

    /// <summary>Windows asks for admin permission, then the installer replaces and reopens the app.</summary>
    private async Task<OperationResult> InstallUpdateAsync()
    {
        VerifiedInstaller installer;
        try
        {
            installer = await Task.Run(_updates.Download);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            return new OperationResult(false, "The update couldn't be downloaded. Try again.");
        }

        using (installer)
        {
            try
            {
                using var _ = Process.Start(new ProcessStartInfo(installer.Path)
                {
                    UseShellExecute = true,
                    Arguments = UpdateService.SilentArguments,
                });
            }
            catch (Win32Exception)
            {
                return new OperationResult(false, "The update was cancelled.");
            }
        }

        _ = Dispatcher.BeginInvoke(ExitApp);
        return new OperationResult(true, "Installing the update.");
    }

    /// <summary>A new scan replaces the last one, so clicking around quickly doesn't stack up disk reads.</summary>
    private async Task<SpaceReport?> ScanSpaceAsync(string path)
    {
        var scan = new CancellationTokenSource();
        Interlocked.Exchange(ref _spaceScan, scan)?.Cancel();
        try
        {
            return await Task.Run(() => SpaceFinder.Scan(path, scan.Token), scan.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static bool ShowInExplorer(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains('"') || !(File.Exists(path) || Directory.Exists(path)))
        {
            throw new CatalogRequestException("That file or folder no longer exists.");
        }

        using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        return true;
    }

    private static bool OpenDriverPage(string vendor)
    {
        var page = GraphicsDrivers.DownloadPage(vendor) ?? throw new CatalogRequestException("There's no download page for that graphics card.");
        using var _ = Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
        return true;
    }

    private async Task<BootReport> ReadBootTimesAsync()
    {
        var diagnostics = await _engine.BootTimesAsync();
        var first = ReadHistory().Select(set => set.CreatedUtc).OrderBy(when => when, StringComparer.Ordinal).FirstOrDefault();
        return new BootReport(diagnostics.Boots, first, diagnostics.SlowApps);
    }

    private static string DownloadsFolder()
    {
        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(downloads, 0, IntPtr.Zero, out var pointer) == 0)
        {
            try
            {
                return Marshal.PtrToStringUni(pointer) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>Recycle Bin only, never a permanent delete, and only for files inside Downloads.</summary>
    private static RecycleResult RecycleDownloads(IReadOnlyList<string> paths)
    {
        var folder = DownloadsFolder();
        int moved = 0, failed = 0;
        foreach (var path in paths)
        {
            if (!OldDownloads.IsInside(folder, path) || !File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                failed++;
                continue;
            }

            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                    Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
                if (File.Exists(path))
                {
                    failed++;
                }
                else
                {
                    moved++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        return new RecycleResult(moved, failed);
    }

    private bool SetTurbo(bool on)
    {
        _tray.Games.SetTurbo(on);
        return _tray.Games.TurboOn;
    }

    /// <summary>Asks Steam to check a game's files. The ID comes from the scan, never from the interface.</summary>
    private OperationResult VerifySteamGame(string gameId)
    {
        var game = _games.FirstOrDefault(item => item.Id == gameId && item.Launcher == "Steam")
            ?? throw new CatalogRequestException("Unknown game.");
        var appId = game.Id["steam-".Length..];
        if (!appId.All(char.IsAsciiDigit))
        {
            throw new CatalogRequestException("Unknown game.");
        }

        using var _ = Process.Start(new ProcessStartInfo($"steam://validate/{appId}") { UseShellExecute = true });
        return new OperationResult(true, "Steam is checking the game's files.");
    }

    private static OperationResult OpenCrashFolder()
    {
        Directory.CreateDirectory(App.CrashFolder);
        using var _ = Process.Start(new ProcessStartInfo(App.CrashFolder) { UseShellExecute = true });
        return new OperationResult(true, "Opened.");
    }

    private OperationResult SetBestDisplay() =>
        _display.SetBest()
            ? new OperationResult(true, "Your display is at its highest refresh rate.")
            : new OperationResult(false, "Windows couldn't change the refresh rate.");

    private IReadOnlyList<ConnectionEntry> ScanConnections()
    {
        var names = _processes.List().ToDictionary(process => process.Id, process => process.Name);
        return _connections.Scan(id => names.GetValueOrDefault(id, "Unknown"));
    }

    private async Task<ReportResult> ExportSettingsAsync()
    {
        var applied = (await Task.Run(ScanTweaks)).Where(item => item.State == TweakStates.Applied).Select(item => item.Id);
        var apps = _softwareIds.Intersect((await Task.Run(_winget.ListSoftware)).Items.Where(item => item.Installed).Select(item => item.Id));
        var text = SettingsFile.Build(applied, ReadSettings(), apps);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "TuneForge settings.json",
            DefaultExt = ".json",
            Filter = "TuneForge settings|*.json",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return new ReportResult(false, null);
        }

        await File.WriteAllTextAsync(dialog.FileName, text);
        return new ReportResult(true, dialog.FileName);
    }

    /// <summary>Tweaks from a file still go through Review. App settings apply straight away.</summary>
    private async Task<ImportResult?> ImportSettingsAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "TuneForge settings|*.json" };
        if (dialog.ShowDialog(this) != true)
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(dialog.FileName);
        var tweakIds = (await Task.Run(ScanTweaks)).Select(item => item.Id).ToList();
        var (file, result) = SettingsFile.Read(text, tweakIds, _softwareIds);
        if (file.Choices is not null)
        {
            SaveSettings(file.Choices);
        }

        if (file.FocusApps is not null)
        {
            _settings.Update(settings => settings with { FocusApps = file.FocusApps });
        }

        return result;
    }

    private static OperationResult OpenTool(string id)
    {
        try
        {
            WindowsToolLinks.Open(id);
            return new OperationResult(true, "Opened.");
        }
        catch (Win32Exception)
        {
            return new OperationResult(false, "That tool couldn't be opened.");
        }
    }

    private IReadOnlyList<GameEntry> ScanGames()
    {
        _games = _gameScanner.Scan(_tray.Games.Profiles);
        return _games;
    }

    /// <summary>Paths come from the scan, never from the interface.</summary>
    private OperationResult SaveGame(GameProfileRequest request)
    {
        var game = _games.FirstOrDefault(item => item.Id == request.GameId)
            ?? throw new CatalogRequestException("Unknown game.");
        _tray.Games.Save(game, request.Boosts);
        Dispatcher.Invoke(() => _tray.Apply(ReadSettings().Choices));
        return new OperationResult(true, $"{game.Title} was saved.");
    }

    private GamingOverview ReadGamingOverview() => new(
        ResizeBarCheck.Read(_runner),
        _plans.List(),
        _settings.Load().FocusApps ?? [],
        _apps.WindowedApps());

    private OperationResult SetPlan(string id) =>
        !_plans.List().Any(plan => plan.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ? throw new CatalogRequestException("Unknown power plan.")
            : _plans.Activate(id)
                ? new OperationResult(true, "The power plan was changed.")
                : new OperationResult(false, "Windows couldn't change the power plan.");

    private OperationResult AddUltimatePlan() =>
        _plans.AddUltimate()
            ? new OperationResult(true, "Ultimate Performance was added.")
            : new OperationResult(false, "Windows couldn't add Ultimate Performance.");

    private OperationResult SaveFocusApps(IReadOnlyList<string> names)
    {
        _settings.Update(settings => settings with { FocusApps = names });
        return new OperationResult(true, "Focus mode was saved.");
    }

    /// <summary>The report is only written where the person chooses, and never leaves the PC.</summary>
    private async Task<ReportResult> SaveReportAsync()
    {
        var report = await Task.Run(_health.BuildReport);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"TuneForge report {DateTime.Now:yyyy-MM-dd}.txt",
            DefaultExt = ".txt",
            Filter = "Text file|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true)
        {
            return new ReportResult(false, null);
        }

        await File.WriteAllTextAsync(dialog.FileName, report);
        return new ReportResult(true, dialog.FileName);
    }

    private DnsStatus ReadDnsStatus() =>
        new(_dns.ReadCurrent(), DnsPresets.All, new FileDnsBackup(JournalLocation.DnsBackupPath).Exists);

    private OperationResult FlushDns() =>
        _runner.Run("ipconfig.exe", ["/flushdns"], TimeSpan.FromMinutes(1)) == 0
            ? new OperationResult(true, "The DNS cache was cleared.")
            : new OperationResult(false, "Windows couldn't clear the DNS cache.");

    /// <summary>The monitor test covers the whole screen, including the taskbar.</summary>
    private void SetFullScreen(bool on)
    {
        if (on)
        {
            _stateBeforeFullScreen = WindowState;
            _styleBeforeFullScreen = WindowStyle;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _styleBeforeFullScreen;
            WindowState = _stateBeforeFullScreen;
        }
    }

    /// <summary>Runs in this process, so the refresh reaches the user's own Explorer windows.</summary>
    private static async Task<T> RefreshAfter<T>(Task<T> work)
    {
        var result = await work;
        await ShellRefresh.RunAsync();
        return result;
    }

    private static T Read<T>(JsonElement payload) =>
        payload.Deserialize<T>(RpcProtocol.Json) ?? throw new EngineException("Invalid request.");

    /// <summary>If History can't be read right now, the list still loads, just without the locked check.</summary>
    private IReadOnlyList<TweakStatus> ScanTweaks()
    {
        IReadOnlySet<string>? ours;
        try
        {
            using var journal = Journal.OpenForReading(JournalLocation.DatabasePath);
            ours = journal?.ActiveWrites() ?? new HashSet<string>();
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            ours = null;
        }

        return _scanner.Scan(ours);
    }

    private static IReadOnlyList<ChangeSetSummary> ReadHistory()
    {
        using var journal = Journal.OpenForReading(JournalLocation.DatabasePath);
        return journal is null ? [] : journal.ListChangeSets();
    }

    private sealed record PreviewPayload(
        [property: System.Text.Json.Serialization.JsonPropertyName("ids")] IReadOnlyList<string> Ids);
}
