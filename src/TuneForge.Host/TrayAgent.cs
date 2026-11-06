using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Threading;
using TuneForge.Contracts;
using TuneForge.Engine.Core;

namespace TuneForge.Host;

/// <summary>The tray icon and the once-a-minute automation check. It runs without admin rights.</summary>
public sealed class TrayAgent : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly nint _iconHandle;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _gameTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly AutomationRunner _runner;
    private readonly GameBooster _games;
    private int _running;
    private int _gamesRunning;

    public TrayAgent(SettingsStore settings, IAutomationActions actions, IGameSettings gameSettings, IPowerPlans plans, IRunningApps apps, Action open, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open TuneForge", null, (_, _) => open());
        menu.Items.Add("Exit", null, (_, _) => exit());

        using var bitmap = DrawIcon();
        _iconHandle = bitmap.GetHicon();
        _icon = new NotifyIcon { Icon = Icon.FromHandle(_iconHandle), Text = "TuneForge", ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => open();

        _runner = new AutomationRunner(settings, actions, () => DateTimeOffset.UtcNow, Notify);
        _timer.Tick += (_, _) => Check();
        _games = new GameBooster(settings, gameSettings, plans, apps, Notify);
        _gameTimer.Tick += (_, _) => CheckGames();
        Guarded(_games.Recover);
    }

    public GameBooster Games => _games;

    /// <summary>The icon shows only while something needs it, and the checks only run then too.</summary>
    public void Apply(SettingsUpdate choices)
    {
        var automations = choices.MemoryAuto || choices.CleanAuto || choices.StartupWatch;
        var games = _games.HasWork;
        _icon.Visible = automations || games || choices.CloseToTray || choices.StartWithWindows;
        _timer.IsEnabled = automations;
        _gameTimer.IsEnabled = games;
    }

    private void CheckGames()
    {
        if (Interlocked.Exchange(ref _gamesRunning, 1) == 0)
        {
            Task.Run(() =>
            {
                Guarded(_games.Tick);
                Interlocked.Exchange(ref _gamesRunning, 0);
            });
        }
    }

    private static void Guarded(Action work)
    {
        try
        {
            work();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A check that can't finish tries again on its next turn.
        }
    }

    private void Notify(string title, string message) =>
        _icon.ShowBalloonTip(5000, title, message, ToolTipIcon.None);

    private void Check()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        Task.Run(() =>
        {
            Guarded(_runner.Tick);
            Interlocked.Exchange(ref _running, 0);
        });
    }

    private static Bitmap DrawIcon()
    {
        var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        using var background = new SolidBrush(Color.FromArgb(0x25, 0x63, 0xEB));
        using var bar = new SolidBrush(Color.White);
        graphics.FillRectangle(background, 0, 0, 32, 32);
        graphics.FillRectangle(bar, 7, 7, 18, 5);
        graphics.FillRectangle(bar, 13, 7, 6, 18);
        return bitmap;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    public void Dispose()
    {
        _timer.Stop();
        _gameTimer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        DestroyIcon(_iconHandle);
    }
}
