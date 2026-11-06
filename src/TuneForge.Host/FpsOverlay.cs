using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TuneForge.Engine.Core;

namespace TuneForge.Host;

/// <summary>
/// A small always-on-top window that shows FPS. It's an ordinary window beside the game, not code
/// inside it, and clicks pass straight through. Games in exclusive fullscreen will cover it.
/// </summary>
public sealed class FpsOverlay : Window
{
    private const int ExtendedStyle = -20;
    private const int Layered = 0x00080000;
    private const int ClickThrough = 0x00000020;
    private const int ToolWindow = 0x00000080;
    private const int NoActivate = 0x08000000;
    private static readonly TimeSpan ReadEvery = TimeSpan.FromMilliseconds(500);

    private readonly TextBlock _text = new()
    {
        Foreground = Brushes.White,
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        FontSize = 18,
        FontWeight = FontWeights.SemiBold,
        Text = "FPS --",
    };

    private readonly DispatcherTimer _timer = new() { Interval = ReadEvery };
    private readonly Func<bool> _keepRunning;

    public FpsOverlay(string captureFile, Func<bool> keepRunning)
    {
        _keepRunning = keepRunning;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = 12;
        Top = 12;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
            Child = _text,
        };

        _timer.Tick += (_, _) =>
        {
            if (!_keepRunning())
            {
                Close();
                return;
            }

            if (FrameStats.LiveFps(captureFile) is { } fps)
            {
                _text.Text = $"FPS {fps:0}";
            }
        };
        Closed += (_, _) => _timer.Stop();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowLongPtr(handle, ExtendedStyle, GetWindowLongPtr(handle, ExtendedStyle) | Layered | ClickThrough | ToolWindow | NoActivate);
        _timer.Start();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
