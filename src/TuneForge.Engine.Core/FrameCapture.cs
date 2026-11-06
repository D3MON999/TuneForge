using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

public interface IDetachedStarter
{
    /// <summary>Starts a program that keeps running after this process exits.</summary>
    bool Start(string path, IReadOnlyList<string> arguments);
}

public sealed class WindowsDetachedStarter : IDetachedStarter
{
    public bool Start(string path, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Starts PresentMon, which reads frame timing from Windows' own event tracing. It runs beside the
/// game and never touches the game's process. Windows only allows this with admin rights.
/// </summary>
public sealed partial class FrameCapture(IDetachedStarter starter, string tool, string output)
{
    public const string SessionName = "TuneForge";
    public const int MaxSeconds = 600;

    public OperationResult Start(FpsRequest request)
    {
        if (!ProcessName().IsMatch(request.Process))
        {
            throw new CatalogRequestException("Unknown game.");
        }

        if (request.Seconds is < 1 or > MaxSeconds)
        {
            throw new CatalogRequestException("That isn't a valid length for a benchmark.");
        }

        if (!File.Exists(tool))
        {
            return new OperationResult(false, "The frame capture tool is missing. Reinstall TuneForge.");
        }

        File.Delete(output);
        string[] common =
        [
            "--process_name", request.Process,
            "--output_file", output,
            "--v1_metrics",
            "--no_console_stats",
            "--no_track_input",
            "--session_name", SessionName,
            "--stop_existing_session",
        ];
        string[] ending = request.Seconds is { } seconds
            ? ["--timed", seconds.ToString(CultureInfo.InvariantCulture), "--terminate_after_timed"]
            : ["--terminate_on_proc_exit"];

        return starter.Start(tool, [.. common, .. ending])
            ? new OperationResult(true, "Measuring frames.")
            : new OperationResult(false, "The frame capture tool couldn't start.");
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._()-]{0,120}\.exe$")]
    private static partial Regex ProcessName();
}

public sealed record FrameSummary(double AverageFps, double OnePercentLowFps, int Frames);

public static class FrameStats
{
    private const string Column = "msBetweenPresents";
    private const int TailBytes = 256 * 1024;
    private const double Second = 1000;

    /// <summary>Reads frame times from PresentMon's file, ignoring a last line it may still be writing.</summary>
    public static IReadOnlyList<double> ReadTimes(IEnumerable<string> lines)
    {
        var times = new List<double>();
        var index = -1;
        var width = 0;
        foreach (var line in lines)
        {
            var cells = line.Split(',');
            if (index < 0)
            {
                index = Array.IndexOf(cells, Column);
                width = cells.Length;
                if (index < 0)
                {
                    return [];
                }

                continue;
            }

            if (cells.Length == width && double.TryParse(cells[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var time) && time > 0)
            {
                times.Add(time);
            }
        }

        return times;
    }

    /// <summary>The 1% low is the average of the slowest 1% of frames, which shows stutter an average hides.</summary>
    public static FrameSummary? Summarize(IReadOnlyList<double> times)
    {
        if (times.Count == 0)
        {
            return null;
        }

        var slowest = Math.Max(1, times.Count / 100);
        return new FrameSummary(
            Second / times.Average(),
            Second / times.OrderDescending().Take(slowest).Average(),
            times.Count);
    }

    /// <summary>FPS over the last second, from a file that's still being written.</summary>
    public static double? LiveFps(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var heading = reader.ReadLine();
            if (heading is null)
            {
                return null;
            }

            if (stream.Length > TailBytes)
            {
                stream.Seek(-TailBytes, SeekOrigin.End);
                reader.DiscardBufferedData();
                reader.ReadLine();
            }

            // The last line may be half written, and a cut-off number would still look valid.
            var text = reader.ReadToEnd();
            var finished = text[..(text.LastIndexOf('\n') + 1)];
            return Recent(ReadTimes(finished.Split('\n', StringSplitOptions.TrimEntries).Prepend(heading)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static double? Recent(IReadOnlyList<double> times)
    {
        var sum = 0d;
        var count = 0;
        for (var index = times.Count - 1; index >= 0 && sum < Second; index--)
        {
            sum += times[index];
            count++;
        }

        return count == 0 ? null : Second * count / sum;
    }
}

/// <summary>Keeps recent benchmark results, so before and after can be compared per game.</summary>
public sealed class BenchmarkHistory(SettingsStore store)
{
    private const int KeepPerGame = 10;

    public IReadOnlyList<BenchmarkRecord> List() => store.Load().Benchmarks ?? [];

    public BenchmarkRecord Add(GameEntry game, string label, FrameSummary summary, DateTime utc)
    {
        var record = new BenchmarkRecord(
            game.Id,
            game.Title,
            label,
            utc.ToString("O", CultureInfo.InvariantCulture),
            Math.Round(summary.AverageFps, 1),
            Math.Round(summary.OnePercentLowFps, 1),
            summary.Frames);
        store.Update(settings =>
        {
            var all = (settings.Benchmarks ?? []).Append(record).ToList();
            var kept = all.Where(item => item.GameId != game.Id).Concat(all.Where(item => item.GameId == game.Id).TakeLast(KeepPerGame));
            return settings with { Benchmarks = kept.ToList() };
        });
        return record;
    }
}
