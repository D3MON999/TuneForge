using System.Net.Http;
using TuneForge.Contracts;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

public class SpeedTestTests
{
    private sealed class FakeSpeed(bool fail = false) : ISpeedProbe
    {
        public (long Bytes, double Seconds) Download(long bytes) => fail ? throw new HttpRequestException() : (bytes, 2);

        public (long Bytes, double Seconds) Upload(long bytes) => (bytes, 1);
    }

    [Fact]
    public void ReportsMegabits()
    {
        var result = new SpeedTest(new FakeSpeed()).Run();

        Assert.Equal(100, result.DownloadMbps);
        Assert.Equal(40, result.UploadMbps);
        Assert.Null(result.Error);
    }

    [Fact]
    public void SurvivesNoInternet()
    {
        Assert.NotNull(new SpeedTest(new FakeSpeed(fail: true)).Run().Error);
    }
}

public class BandwidthLimitsTests
{
    [Fact]
    public void SetsAPolicyForOneProgram()
    {
        var tool = new FakeTool();

        Assert.True(new BandwidthLimits(tool).Set(new BandwidthRule("backup.exe", 10)).Ok);

        Assert.Contains("ThrottleRateActionBitsPerSecond 10000000", tool.Calls[^1].Arguments[^1]);
    }

    [Theory]
    [InlineData("game")]
    [InlineData("x'; Remove-Item C:\\; '.exe")]
    [InlineData("..\\evil.exe")]
    public void RefusesAnythingButAProgramName(string program)
    {
        var tool = new FakeTool();

        Assert.Throws<CatalogRequestException>(() => new BandwidthLimits(tool).Set(new BandwidthRule(program, 10)));
        Assert.Empty(tool.Calls);
    }

    [Fact]
    public void RefusesSillyLimits()
    {
        Assert.Throws<CatalogRequestException>(() => new BandwidthLimits(new FakeTool()).Set(new BandwidthRule("a.exe", 0)));
    }

    [Fact]
    public void ReadsExistingLimits()
    {
        var rules = new BandwidthLimits(new FakeTool(new ToolOutput(0, "backup.exe|10000000\r\nbad line\r\n"))).List();

        Assert.Equal(new BandwidthRule("backup.exe", 10), Assert.Single(rules));
    }
}

public class GpuChecksTests
{
    [Fact]
    public void ReadsHealthValues()
    {
        var gpu = GpuChecks.Parse("NVIDIA GeForce RTX 3060, 517.14, 55, 30, 28.41, P8\r\n");

        Assert.Equal(new GpuHealth("NVIDIA GeForce RTX 3060", "517.14", 55, 30, 28.41, "P8"), gpu);
    }

    [Fact]
    public void HandlesMissingValuesAndBadOutput()
    {
        Assert.Null(GpuChecks.Parse("NVIDIA, 517.14, 55, [N/A], [N/A], P8")!.PowerWatts);
        Assert.Null(GpuChecks.Parse(string.Empty));
    }
}

public class BiosRestartTests
{
    [Fact]
    public void UnsupportedPcsGetAnotherWay()
    {
        var result = new BiosRestart(new FakeRunner(1)).Restart();

        Assert.False(result.Ok);
        Assert.Contains("during startup", result.Message);
    }

    [Fact]
    public void WarnsBeforeTheRestart()
    {
        Assert.True(new BiosRestart(new FakeRunner(0)).Restart().RestartNeeded);
        Assert.True(new BiosRestart(new FakeRunner(0)).Cancel().Ok);
    }
}

public sealed class RegistryBackupTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tf-reg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    [Fact]
    public void KeepsOnlyTheLatestCopies()
    {
        var runner = new FakeRunner(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var backup = new RegistryBackup(runner, _folder);

        for (var day = 1; day <= 7; day++)
        {
            Assert.True(backup.Run(new DateTime(2026, 11, day)).Ok);
        }

        Assert.Equal(5, Directory.GetDirectories(_folder).Length);
        Assert.DoesNotContain(Directory.GetDirectories(_folder), path => path.Contains("2026-11-01"));
    }

    [Fact]
    public void ReportsAFailedExport()
    {
        Assert.False(new RegistryBackup(new FakeRunner(1), _folder).Run(DateTime.Now).Ok);
    }
}

public class ChocolateyFallbackTests : IDisposable
{
    private readonly string _choco = Path.GetTempFileName();

    public void Dispose()
    {
        File.Delete(_choco);
        GC.SuppressFinalize(this);
    }

    private static readonly SoftwareItem[] Software = [new("Odd.App", "Odd", "n", "Utilities", "oddapp")];

    [Fact]
    public void TriesChocolateyOnlyAfterWingetFails()
    {
        var tool = new FakeTool(new ToolOutput(unchecked((int)0x8A150014), string.Empty), new ToolOutput(0, string.Empty));

        var result = new WingetClient(tool, Software, @"C:\fake\winget.exe", _choco).Install("Odd.App");

        Assert.True(result.Ok);
        Assert.Contains("Chocolatey", result.Message);
        Assert.Equal(_choco, tool.Calls[1].Path);
        Assert.Equal(["install", "oddapp", "-y", "--no-progress"], tool.Calls[1].Arguments);
    }

    [Fact]
    public void LeavesWingetsErrorIfChocolateyIsMissing()
    {
        var tool = new FakeTool(new ToolOutput(unchecked((int)0x8A150014), string.Empty));

        var result = new WingetClient(tool, Software, @"C:\fake\winget.exe", @"C:\missing\choco.exe").Install("Odd.App");

        Assert.False(result.Ok);
        Assert.Single(tool.Calls);
    }
}
