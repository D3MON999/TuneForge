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

    public MainWindow()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var registry = new WindowsRegistryStore(identity.User!);
        var paths = UserPaths.For(identity.User!);
        _scanner = new TweakScanner(TweakCatalog.LoadEmbedded(), registry, Environment.OSVersion.Version.Build);
        _cleanup = new CleanupService(CleanupService.Standard(paths), new WindowsRecycleBin());
        _startup = new StartupService(registry, paths);
        _services = new ServiceManager(registry, new WindowsServiceSource());
        var runner = new WindowsProcessRunner();
        _features = new FeatureService(runner, new WmiFeatureStateReader());
        _builtIn = new BuiltInAppService(runner, AppCatalogs.LoadBuiltIn());
        _winget = new WingetClient(runner, AppCatalogs.LoadSoftware());

        InitializeComponent();
        Loaded += async (_, _) => await StartWebAsync();
        Closed += (_, _) => _engine.Dispose();
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
