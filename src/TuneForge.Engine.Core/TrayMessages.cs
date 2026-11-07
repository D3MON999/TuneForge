using System.Globalization;
using TuneForge.Contracts;

namespace TuneForge.Engine.Core;

/// <summary>Short messages for the tray's pop-up notifications.</summary>
public static class TrayMessages
{
    public static string Memory(MemoryResult result)
    {
        if (result.Skipped)
        {
            return "Skipped, because anti-cheat is running.";
        }

        var freed = result.FreedBytes / (1024d * 1024);
        return freed < 1
            ? "Memory is already in good shape."
            : string.Create(CultureInfo.InvariantCulture, $"Freed {(freed >= 1024 ? $"{freed / 1024:0.0} GB" : $"{freed:0} MB")}.");
    }
}
