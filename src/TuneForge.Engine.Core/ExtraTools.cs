using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

public interface ISpeedProbe
{
    /// <summary>Downloads a test file and returns the bytes received and the seconds taken.</summary>
    (long Bytes, double Seconds) Download(long bytes);

    (long Bytes, double Seconds) Upload(long bytes);
}

/// <summary>Uses Cloudflare's public speed test files. It only runs when someone clicks the button.</summary>
public sealed class CloudflareSpeedProbe : ISpeedProbe, IDisposable
{
    private const string Host = "https://speed.cloudflare.com";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public (long Bytes, double Seconds) Download(long bytes)
    {
        var clock = Stopwatch.StartNew();
        using var response = _http.GetAsync($"{Host}/__down?bytes={bytes.ToString(CultureInfo.InvariantCulture)}", HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using var stream = response.Content.ReadAsStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            total += read;
        }

        return (total, clock.Elapsed.TotalSeconds);
    }

    public (long Bytes, double Seconds) Upload(long bytes)
    {
        var clock = Stopwatch.StartNew();
        using var content = new ByteArrayContent(new byte[bytes]);
        using var response = _http.PostAsync($"{Host}/__up", content).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return (bytes, clock.Elapsed.TotalSeconds);
    }

    public void Dispose() => _http.Dispose();
}

public sealed class SpeedTest(ISpeedProbe probe)
{
    private const long DownloadBytes = 25_000_000;
    private const long UploadBytes = 5_000_000;
    private const double BitsPerMegabit = 1_000_000;

    public SpeedResult Run()
    {
        try
        {
            return new SpeedResult(Mbps(probe.Download(DownloadBytes)), Mbps(probe.Upload(UploadBytes)), null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return new SpeedResult(null, null, "The test server couldn't be reached. Check your connection and try again.");
        }
    }

    private static double Mbps((long Bytes, double Seconds) sample) => sample.Bytes * 8 / sample.Seconds / BitsPerMegabit;
}

/// <summary>
/// Uses Windows' own network QoS policies, so no driver is needed. They limit uploads only,
/// because Windows has no built-in way to limit downloads per app.
/// </summary>
public sealed partial class BandwidthLimits(IToolRunner runner)
{
    private const string Prefix = "TuneForge ";
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(1);
    private static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public IReadOnlyList<BandwidthRule> List()
    {
        var output = runner.Capture(
            PowerShell,
            ["-NoProfile", "-NonInteractive", "-Command", $"Get-NetQosPolicy | Where-Object {{ $_.Name -like '{Prefix}*' }} | ForEach-Object {{ $_.AppPathNameMatchCondition + '|' + $_.ThrottleRateAction }}"],
            Limit);
        return output.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('|'))
            .Where(parts => parts.Length == 2 && long.TryParse(parts[1], CultureInfo.InvariantCulture, out _))
            .Select(parts => new BandwidthRule(parts[0], long.Parse(parts[1], CultureInfo.InvariantCulture) / 1_000_000))
            .ToList();
    }

    public OperationResult Set(BandwidthRule rule)
    {
        Check(rule.Program);
        if (rule.Mbps is < 1 or > 10_000)
        {
            throw new CatalogRequestException("Enter a limit between 1 and 10000 Mbps.");
        }

        Remove(rule.Program);
        var bits = (rule.Mbps * 1_000_000).ToString(CultureInfo.InvariantCulture);
        return Run($"New-NetQosPolicy -Name '{Prefix}{rule.Program}' -AppPathNameMatchCondition '{rule.Program}' -ThrottleRateActionBitsPerSecond {bits} | Out-Null", $"{rule.Program} uploads are limited to {rule.Mbps} Mbps.");
    }

    public OperationResult Remove(string program)
    {
        Check(program);
        return Run($"Remove-NetQosPolicy -Name '{Prefix}{program}' -Confirm:$false -ErrorAction SilentlyContinue", $"The limit on {program} was removed.");
    }

    private OperationResult Run(string command, string done) =>
        runner.Capture(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", command], Limit).ExitCode == 0
            ? new OperationResult(true, done)
            : new OperationResult(false, "Windows couldn't change the limit.");

    /// <summary>The name goes into a PowerShell command, so only plain program names get through.</summary>
    private static void Check(string program)
    {
        if (!ProgramName().IsMatch(program))
        {
            throw new CatalogRequestException("Enter a program file name, like game.exe.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._-]{0,80}\.exe$")]
    private static partial Regex ProgramName();
}

public sealed record GpuHealth(string Name, string Driver, int? TemperatureC, int? FanPercent, double? PowerWatts, string Performance);

/// <summary>Reads the health values NVIDIA's own tool reports. Other brands return nothing.</summary>
public static class GpuChecks
{
    public static GpuHealth? Read(IToolRunner runner)
    {
        var tool = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        if (!File.Exists(tool))
        {
            return null;
        }

        var output = runner.Capture(
            tool,
            ["--query-gpu=name,driver_version,temperature.gpu,fan.speed,power.draw,pstate", "--format=csv,noheader,nounits"],
            TimeSpan.FromSeconds(5));
        return output.ExitCode == 0 ? Parse(output.Text) : null;
    }

    public static GpuHealth? Parse(string text)
    {
        var parts = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split(',', StringSplitOptions.TrimEntries);
        return parts is not { Length: 6 }
            ? null
            : new GpuHealth(parts[0], parts[1], Whole(parts[2]), Whole(parts[3]), Number(parts[4]), parts[5]);
    }

    private static int? Whole(string text) => int.TryParse(text, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static double? Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>Restarts straight into the firmware setup screen, with a delay so it can be cancelled.</summary>
public sealed class BiosRestart(IProcessRunner runner)
{
    private const int DelaySeconds = 30;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    public OperationResult Restart() =>
        runner.Run("shutdown.exe", ["/r", "/fw", "/t", DelaySeconds.ToString(CultureInfo.InvariantCulture)], Limit) == 0
            ? new OperationResult(true, $"Your PC restarts into BIOS in {DelaySeconds} seconds. Save your work.", true)
            : new OperationResult(false, "This PC doesn't support restarting into BIOS from Windows. Press Del or F2 during startup instead.");

    public OperationResult Cancel() =>
        runner.Run("shutdown.exe", ["/a"], Limit) == 0
            ? new OperationResult(true, "The restart was cancelled.")
            : new OperationResult(false, "There's no restart to cancel.");
}

/// <summary>Exports the registry to a dated folder, as a safety copy. It only reads the registry.</summary>
public sealed class RegistryBackup(IProcessRunner runner, string folder)
{
    private const int KeepCopies = 5;
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);
    private static readonly string[] Hives = ["HKCU", "HKLM\\SOFTWARE"];

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "TuneForge Backups");

    public OperationResult Run(DateTime when)
    {
        var target = Path.Combine(folder, when.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(target);
        foreach (var hive in Hives)
        {
            var file = Path.Combine(target, hive.Replace('\\', '-') + ".reg");
            if (runner.Run("reg.exe", ["export", hive, file, "/y"], Limit) != 0)
            {
                return new OperationResult(false, $"{hive} couldn't be backed up.");
            }
        }

        foreach (var old in Directory.GetDirectories(folder).Order().Reverse().Skip(KeepCopies))
        {
            Directory.Delete(old, true);
        }

        return new OperationResult(true, $"Registry backed up to {target}.");
    }
}
