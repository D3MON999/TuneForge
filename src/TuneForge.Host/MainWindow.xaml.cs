using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace TuneForge.Host;

public partial class MainWindow : Window
{
    private const string AppHost = "app.tuneforge.local";
    private static readonly string AppOrigin = $"https://{AppHost}";

    private readonly EngineClient _engine = new();

    public MainWindow()
    {
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
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            id = message.RootElement.GetProperty("id").GetString() ?? string.Empty;
            op = message.RootElement.GetProperty("op").GetString() ?? string.Empty;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return;
        }

        object reply;
        try
        {
            reply = op switch
            {
                "engine.connect" => new { id, ok = true, result = await _engine.ConnectAndPingAsync() },
                _ => new { id, ok = false, error = "Unknown operation." },
            };
        }
        catch (EngineCancelledException)
        {
            reply = new { id, ok = false, error = "Admin permission was declined." };
        }
        catch (Exception exception) when (exception is EngineException or IOException or TimeoutException)
        {
            reply = new { id, ok = false, error = "The engine could not be reached." };
        }

        Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(reply));
    }
}
