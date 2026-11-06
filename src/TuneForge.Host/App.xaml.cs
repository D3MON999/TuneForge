using System.Threading;
using System.Windows;
using TuneForge.Engine.Core;

namespace TuneForge.Host;

/// <summary>Keeps one copy running per sign-in. Opening TuneForge again shows the running copy.</summary>
public partial class App : Application
{
    private const string InstanceName = @"Local\TuneForge.Instance";
    private const string ShowName = @"Local\TuneForge.Show";

    private Mutex? _instance;
    private EventWaitHandle? _showRequest;
    private RegisteredWaitHandle? _showWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(true, InstanceName, out var first);
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName);
        if (!first)
        {
            _showRequest.Set();
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        _showWait = ThreadPool.RegisterWaitForSingleObject(
            _showRequest,
            (_, _) => Dispatcher.BeginInvoke(window.ShowFromTray),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        if (!e.Args.Contains(WindowsAutostart.TrayArgument))
        {
            window.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showRequest?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
