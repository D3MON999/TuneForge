using System.Runtime.InteropServices;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Host;

/// <summary>Colours Windows' own title bar, which keeps snapping and resizing working.</summary>
internal static class TitleBar
{
    private const int UseDarkMode = 20;
    private const int UseDarkModeOlder = 19;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;
    private const int White = 0xFFFFFF;
    private const int NearBlack = 0x111111;

    public static void Apply(nint window, string color, bool dark)
    {
        var caption = TitleBarColor.ToWindowsColor(color) ?? throw new CatalogRequestException("That colour isn't valid.");

        // Windows 10 only offers dark or light. The exact colours need Windows 11 and are ignored before that.
        if (Set(window, UseDarkMode, dark ? 1 : 0) != 0)
        {
            Set(window, UseDarkModeOlder, dark ? 1 : 0);
        }

        Set(window, BorderColor, caption);
        Set(window, CaptionColor, caption);
        Set(window, TextColor, dark ? White : NearBlack);
    }

    private static int Set(nint window, int attribute, int value) =>
        DwmSetWindowAttribute(window, attribute, ref value, sizeof(int));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
