using System.Globalization;
using System.Text.RegularExpressions;

namespace TuneForge.Engine.Core;

public static partial class TitleBarColor
{
    /// <summary>Turns #rrggbb into the blue-green-red number Windows wants. Anything else is refused.</summary>
    public static int? ToWindowsColor(string color)
    {
        if (!Hex().IsMatch(color))
        {
            return null;
        }

        var value = int.Parse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ((value & 0xFF) << 16) | (value & 0xFF00) | (value >> 16);
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex Hex();
}
