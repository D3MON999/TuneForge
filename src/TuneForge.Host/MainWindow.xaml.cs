using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
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
    private volatile IReadOnlyList<GameEntry> _games = [];
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
        _health = new HealthService(_storage, _events, _startup, _cleanup, probe);
        _trimmer = new WindowsMemoryTrimmer(probe);
        _plans = new WindowsPowerPlans(_runner);
        _gameScanner = new GameScanner(GameSources.Detect());
        _tray = new TrayAgent(
            _settings,
            new AutomationActions(_cleanup, _startup, probe, _trimmer),
            new WindowsGameSettings(),
            _plans,
            _apps,
            ShowFromTray,
            ExitApp);
        _tray.Apply(ReadSettings().Choices);

        InitializeComponent();
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

        Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(reply, RpcProtocol.Json));
    }

    /// <summary>The complete list of what the interface may ask for.</summary>
    private async Task<object?> RunAsync(string op, JsonElement payload) => op switch
    {
        "engine.connect" => await _engine.PingAsync(),
        "tweaks.scan" => await Task.Run(_scanner.Scan),
        "tweaks.preview" => await Task.Run(() => _scanner.Preview(Read<PreviewPayload>(payload).Ids)),
        "tweaks.apply" => await RefreshAfter(_engine.ApplyAsync(Read<ApplyRequest>(payload))),
        "history.list" => await Task.Run(ReadHistory),
        "history.undo" => await RefreshAfter(_engine.UndoAsync(Read<UndoRequest>(payload).ChangeSetId)),
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

    private static IReadOnlyList<ChangeSetSummary> ReadHistory()
    {
        using var journal = Journal.OpenForReading(JournalLocation.DatabasePath);
        return journal is null ? [] : journal.ListChangeSets();
    }

    private sealed record PreviewPayload(
        [property: System.Text.Json.Serialization.JsonPropertyName("ids")] IReadOnlyList<string> Ids);
}
