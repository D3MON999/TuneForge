using TuneForge.Contracts;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

public sealed class HealthIgnoreTests : IDisposable
{
    private static readonly StorageReport Fine = new(
        [new DiskInfo("SSD", "SSD", 100, "healthy")],
        [new VolumeInfo(@"C:\", "", 1000, 500)]);

    private static readonly StorageReport Failing = new(
        [new DiskInfo("SSD", "SSD", 100, "unhealthy")],
        [new VolumeInfo(@"C:\", "", 1000, 100)]);

    private readonly string _folder = Directory.CreateTempSubdirectory("tf-ignore").FullName;

    private SettingsStore Store() => new(Path.Combine(_folder, "settings.json"));

    private static HealthInput Input(StorageReport storage, int crashes = 0, int errors = 0, int startup = 3) =>
        new(storage, @"C:\", crashes, errors, startup, 40, 1, 0);

    private static Dictionary<string, string> Ignored(string level, params string[] ids) => ids.ToDictionary(id => id, _ => level);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void IgnoringEveryProblemGives100AndKeepsTheFullScore()
    {
        var report = HealthScorer.Score(Input(Fine, crashes: 1, errors: 500, startup: 30), Ignored("warn", "crashes", "errors", "startup"));

        Assert.Equal(100, report.Score);
        Assert.Equal(80, report.FullScore);
        Assert.Equal(3, report.Checks.Count(check => check.Ignored));
    }

    [Fact]
    public void AProblemThatIsNotIgnoredStillCounts()
    {
        var report = HealthScorer.Score(Input(Fine, crashes: 1, errors: 500), Ignored("warn", "errors"));

        Assert.Equal(90, report.Score);
        Assert.Equal(85, report.FullScore);
    }

    [Fact]
    public void ACheckThatGetsWorseCountsAgain()
    {
        var report = HealthScorer.Score(Input(Failing), Ignored("warn", "disks", "space"));

        // The failing disk is worse than a warning. Low space is still only a warning, so it stays ignored.
        Assert.Equal(70, report.Score);
        Assert.Equal(60, report.FullScore);
        var disk = report.Checks.Single(check => check.Id == "disks");
        Assert.False(disk.Ignored);
        Assert.True(disk.Worsened);
        var space = report.Checks.Single(check => check.Id == "space");
        Assert.True(space.Ignored);
        Assert.False(space.Worsened);
    }

    [Fact]
    public void IgnoringItAgainAtTheWorseLevelHolds()
    {
        var report = HealthScorer.Score(Input(Failing), Ignored("bad", "disks", "space"));

        Assert.Equal(100, report.Score);
        var disk = report.Checks.Single(check => check.Id == "disks");
        Assert.Equal("bad", disk.Status);
        Assert.Contains("failing", disk.Detail);
        Assert.True(disk.Ignored);
        Assert.False(disk.Worsened);
    }

    [Fact]
    public void ACheckThatImprovesStaysIgnored()
    {
        var report = HealthScorer.Score(Input(Fine, crashes: 1), Ignored("bad", "crashes"));

        Assert.Equal(100, report.Score);
        Assert.True(report.Checks.Single(check => check.Id == "crashes").Ignored);
    }

    [Fact]
    public void ACheckThatRecoversIsIgnoredAgainWithoutAsking()
    {
        var worse = HealthScorer.Score(Input(Fine, crashes: 5), Ignored("warn", "crashes"));
        var better = HealthScorer.Score(Input(Fine, crashes: 1), Ignored("warn", "crashes"));

        Assert.Equal(80, worse.Score);
        Assert.Equal(100, better.Score);
    }

    [Fact]
    public void AChoiceSavedBeforeLevelsExistedActsLikeAWarning()
    {
        Store().Update(settings => settings with { IgnoredChecks = ["disks"] });

        var ignored = Store().LoadIgnored();

        Assert.Equal("warn", ignored["disks"]);
        Assert.True(HealthScorer.Score(Input(Failing), ignored).Checks.Single(check => check.Id == "disks").Worsened);
    }

    [Fact]
    public void TheLevelIsRememberedAndClearedWithTheChoice()
    {
        Store().SetIgnoredCheck("disks", true, "bad");
        Assert.Equal("bad", Store().LoadIgnored()["disks"]);

        Store().SetIgnoredCheck("disks", false);
        Assert.Empty(Store().LoadIgnored());
        Assert.Empty(Store().Load().IgnoredLevels!);
    }

    [Fact]
    public void WithNothingIgnoredBothScoresMatch()
    {
        var report = HealthScorer.Score(Input(Fine, crashes: 1));

        Assert.Equal(report.Score, report.FullScore);
        Assert.DoesNotContain(report.Checks, check => check.Ignored);
    }

    [Fact]
    public void EveryCheckCanBeIgnored()
    {
        Assert.Equal(HealthScorer.Score(Input(Fine)).Checks.Select(check => check.Id), HealthScorer.CheckIds);
    }

    [Fact]
    public void AnUnknownCheckIsRefused()
    {
        var service = new HealthService(null!, null!, null!, null!, null!, Store());

        Assert.Throws<CatalogRequestException>(() => service.SetIgnored(new HealthIgnoreRequest("nonsense", true)));
        Assert.Null(Store().Load().IgnoredChecks);
    }

    [Fact]
    public void TheChoiceSurvivesARestartAndCanBeUndone()
    {
        Store().SetIgnoredCheck("space", true);
        Store().SetIgnoredCheck("space", true);
        Store().SetIgnoredCheck("crashes", true);

        Assert.Equal(["space", "crashes"], new SettingsStore(Path.Combine(_folder, "settings.json")).Load().IgnoredChecks);

        Store().SetIgnoredCheck("space", false);

        Assert.Equal(["crashes"], Store().Load().IgnoredChecks);
    }

    [Fact]
    public void SavingOtherSettingsKeepsTheChoice()
    {
        Store().SetIgnoredCheck("space", true);

        Store().SaveChoices(new SettingsUpdate(CloseToTray: true));

        Assert.Equal(["space"], Store().Load().IgnoredChecks);
    }

    [Fact]
    public void TheReportSaysWhatIsIgnored()
    {
        var text = ReportWriter.Build(
            DateTimeOffset.UnixEpoch,
            new SystemSummary("PC", "Windows 11", "26300", "Board", "CPU", 8, 16, ["GPU"], 1L << 33, 60),
            new HealthReport(100, [new HealthCheck("space", "Free space", "warn", "Low.", Ignored: true)], 90),
            Fine,
            []);

        Assert.Contains("Health score: 100 out of 100 (90 with everything counted)", text);
        Assert.Contains("[warn, ignored] Free space: Low.", text);
    }

    [Fact]
    public void TheReportSaysWhatGotWorse()
    {
        var text = ReportWriter.Build(
            DateTimeOffset.UnixEpoch,
            new SystemSummary("PC", "Windows 11", "26300", "Board", "CPU", 8, 16, ["GPU"], 1L << 33, 60),
            new HealthReport(70, [new HealthCheck("disks", "Disk health", "bad", "Failing.", Worsened: true)], 70),
            Fine,
            []);

        Assert.Contains("[bad, worse since ignored] Disk health: Failing.", text);
    }

    [Theory]
    [InlineData("#000000", 0x000000)]
    [InlineData("#112233", 0x332211)]
    [InlineData("#f3f4f6", 0xF6F4F3)]
    public void TitleBarColoursBecomeWindowsOrder(string color, int expected)
    {
        Assert.Equal(expected, TitleBarColor.ToWindowsColor(color));
    }

    [Theory]
    [InlineData("black")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("")]
    public void BadTitleBarColoursAreRefused(string color)
    {
        Assert.Null(TitleBarColor.ToWindowsColor(color));
    }
}
