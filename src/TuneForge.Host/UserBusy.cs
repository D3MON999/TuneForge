using System.Diagnostics;
using System.Runtime.InteropServices;
using TuneForge.Engine.Core;

namespace TuneForge.Host;

/// <summary>Asks Windows whether someone is busy, without touching any other program.</summary>
internal static class UserBusy
{
    private const int Busy = 2;
    private const int FullScreenGame = 3;
    private const int Presenting = 4;

    public static bool Now() =>
        (SHQueryUserNotificationState(out var state) == 0 && state is Busy or FullScreenGame or Presenting)
        || ProtectedProcesses.AnyGameRunning(Process.GetProcesses().Select(process => process.ProcessName).ToList());

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
