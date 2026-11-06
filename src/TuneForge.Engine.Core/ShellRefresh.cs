using System.Runtime.InteropServices;

namespace TuneForge.Engine.Core;

/// <summary>
/// Tells Windows to re-read settings it has cached. Without this, Explorer keeps showing the old
/// state after a registry change, then picks up the new value at some later moment.
/// </summary>
public static class ShellRefresh
{
    private const int BroadcastWindow = 0xFFFF;
    private const uint SettingChange = 0x001A;
    private const uint AbortIfHung = 0x0002;
    private const uint TimeoutMilliseconds = 1000;
    private const int AssociationChanged = 0x08000000;

    /// <summary>A refresh is a courtesy, so a failure here must never fail the change itself.</summary>
    public static Task RunAsync()
    {
        var finished = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                Notify();
                RefreshOpenWindows();
            }
            catch (Exception exception) when (exception is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // Explorer may not be running, or may refuse. The setting is still saved.
            }
            finally
            {
                finished.SetResult();
            }
        })
        {
            IsBackground = true,
        };

        // The Shell.Application object needs a single-threaded apartment.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return finished.Task;
    }

    private static void Notify()
    {
        SHChangeNotify(AssociationChanged, 0, IntPtr.Zero, IntPtr.Zero);
        foreach (var area in new[] { "ShellState", "WindowMetrics", "Policy" })
        {
            SendMessageTimeoutW(
                new IntPtr(BroadcastWindow),
                SettingChange,
                IntPtr.Zero,
                area,
                AbortIfHung,
                TimeoutMilliseconds,
                out _);
        }
    }

    private static void RefreshOpenWindows()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
        {
            return;
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        foreach (dynamic window in shell.Windows())
        {
            window.Refresh();
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr window,
        uint message,
        IntPtr wParam,
        string lParam,
        uint flags,
        uint timeout,
        out IntPtr result);
}
