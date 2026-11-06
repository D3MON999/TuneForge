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

    public MainWindow()
    {
        using var identity = WindowsIdentity.GetCurrent();
        _scanner = new TweakScanner(
            TweakCatalog.LoadEmbedded(),
            new WindowsRegistryStore(identity.User!),
            Environment.OSVersion.Version.Build);

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
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            reply = new { id, ok = false, error = "The engine could not be reached." };
        }

        Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(reply));
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
        _ => throw new EngineException("Unknown operation."),
    };

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
