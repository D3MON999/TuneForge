using TuneForge.Contracts;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

public sealed class FrameCaptureTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tf-frames-" + Guid.NewGuid().ToString("N"));
    private readonly string _tool;
    private readonly string _output;

    public FrameCaptureTests()
    {
        Directory.CreateDirectory(_folder);
        _tool = Path.Combine(_folder, "PresentMon.exe");
        _output = Path.Combine(_folder, "frames.csv");
        File.WriteAllText(_tool, "tool");
    }

    public void Dispose() => Directory.Delete(_folder, true);

    private sealed class FakeStarter : IDetachedStarter
    {
        public List<string> Arguments { get; } = [];

        public bool Start(string path, IReadOnlyList<string> arguments)
        {
            Arguments.AddRange(arguments);
            return true;
        }
    }

    [Fact]
    public void Benchmark_StopsItselfAndReplacesTheOldFile()
    {
        File.WriteAllText(_output, "old");
        var starter = new FakeStarter();

        Assert.True(new FrameCapture(starter, _tool, _output).Start(new FpsRequest("RDR2.exe", 60)).Ok);

        Assert.False(File.Exists(_output));
        Assert.Contains("--terminate_after_timed", starter.Arguments);
        Assert.Contains("60", starter.Arguments);
        Assert.Contains("RDR2.exe", starter.Arguments);
        Assert.Contains("--v1_metrics", starter.Arguments);
    }

    [Fact]
    public void Overlay_StopsWhenTheGameCloses()
    {
        var starter = new FakeStarter();

        new FrameCapture(starter, _tool, _output).Start(new FpsRequest("TEKKEN 7.exe"));

        Assert.Contains("--terminate_on_proc_exit", starter.Arguments);
        Assert.DoesNotContain("--timed", starter.Arguments);
    }

    [Theory]
    [InlineData("--etl_file")]
    [InlineData("game")]
    [InlineData("..\\evil.exe")]
    [InlineData("a\" --output_file C:\\x.exe")]
    public void RefusesAnythingButAProgramName(string process)
    {
        var starter = new FakeStarter();

        Assert.Throws<CatalogRequestException>(() => new FrameCapture(starter, _tool, _output).Start(new FpsRequest(process)));
        Assert.Empty(starter.Arguments);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100000)]
    public void RefusesBadLengths(int seconds)
    {
        Assert.Throws<CatalogRequestException>(() => new FrameCapture(new FakeStarter(), _tool, _output).Start(new FpsRequest("a.exe", seconds)));
    }

    [Fact]
    public void SaysWhenTheToolIsMissing()
    {
        Assert.False(new FrameCapture(new FakeStarter(), Path.Combine(_folder, "none.exe"), _output).Start(new FpsRequest("a.exe", 5)).Ok);
    }

    [Fact]
    public void LiveFps_ReadsAFileStillBeingWritten()
    {
        const string heading = "Application,ProcessID,msBetweenPresents";
        using (var writer = new StreamWriter(new FileStream(_output, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)))
        {
            writer.WriteLine(heading);
            for (var frame = 0; frame < 300; frame++)
            {
                writer.WriteLine("game.exe,1,10.0");
            }

            writer.Write("game.exe,1,1");
            writer.Flush();

            Assert.Equal(100, FrameStats.LiveFps(_output)!.Value, 1);
        }
    }

    [Fact]
    public void LiveFps_IsNullWithoutAFile()
    {
        Assert.Null(FrameStats.LiveFps(Path.Combine(_folder, "none.csv")));
    }
}

public class FrameStatsTests
{
    [Fact]
    public void FindsTheColumnByNameAndSkipsPartLines()
    {
        var times = FrameStats.ReadTimes(["App,msBetweenPresents,Other", "g,16.5,1", "g,bad,1", "g,0,1", "g,20"]);

        Assert.Equal([16.5], times);
    }

    [Fact]
    public void ReturnsNothingForAnUnknownFormat()
    {
        Assert.Empty(FrameStats.ReadTimes(["Something,Else", "1,2"]));
        Assert.Empty(FrameStats.ReadTimes([]));
    }

    [Fact]
    public void AverageAndOnePercentLowComeFromFrameTimes()
    {
        var times = Enumerable.Repeat(10d, 99).Append(100d).ToList();

        var summary = FrameStats.Summarize(times)!;

        Assert.Equal(1000 / 10.9, summary.AverageFps, 3);
        Assert.Equal(10, summary.OnePercentLowFps, 3);
        Assert.Equal(100, summary.Frames);
    }

    [Fact]
    public void NoFramesMeansNoSummary()
    {
        Assert.Null(FrameStats.Summarize([]));
        Assert.Null(FrameStats.Recent([]));
    }

    [Fact]
    public void RecentUsesOnlyTheLastSecond()
    {
        var times = Enumerable.Repeat(50d, 100).Concat(Enumerable.Repeat(10d, 100)).ToList();

        Assert.Equal(100, FrameStats.Recent(times)!.Value, 1);
    }
}

public sealed class BenchmarkHistoryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tf-bench-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private static GameEntry Game(string id) => new(id, id, "Steam", "C:\\", "C:\\g.exe", ["g"], null, new GameBoosts());

    [Fact]
    public void KeepsResultsAndLimitsThemPerGame()
    {
        var history = new BenchmarkHistory(new SettingsStore(Path.Combine(_folder, "settings.json")));
        var summary = new FrameSummary(99.96, 50.04, 6000);

        var record = history.Add(Game("a"), "Before", summary, new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc));
        for (var run = 0; run < 12; run++)
        {
            history.Add(Game("b"), "After", summary, DateTime.UtcNow);
        }

        Assert.Equal(100, record.AverageFps);
        Assert.Equal(50, record.OnePercentLowFps);
        Assert.Single(history.List(), item => item.GameId == "a");
        Assert.Equal(10, history.List().Count(item => item.GameId == "b"));
    }
}
