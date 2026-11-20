using System.Net;
using TuneForge.Contracts;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

public class CrashReaderTests
{
    private static WindowsEvent Event(string when, string source, int id, string message) =>
        new(when, "System", source, id, "error", message);

    [Fact]
    public void ExplainsABlueScreenAndMergesItsRestart()
    {
        var crashes = CrashReader.Summarize(
        [
            Event("2026-11-10T20:00:30Z", "Microsoft-Windows-WER-SystemErrorReporting", 1001,
                @"The computer has rebooted from a bugcheck. The bugcheck was: 0x00000116 (0xffffa00, 0xfffff80, 0x0, 0x2). A dump was saved in: C:\WINDOWS\Minidump\111026-9406-01.dmp."),
            Event("2026-11-10T20:00:05Z", "Microsoft-Windows-Kernel-Power", 41, "The system has rebooted without cleanly shutting down first."),
            Event("2026-11-02T08:00:00Z", "Microsoft-Windows-Kernel-Power", 41, "The system has rebooted without cleanly shutting down first."),
            Event("2026-11-03T08:00:00Z", "Disk", 7, "Bad block."),
        ]);

        Assert.Equal(2, crashes.Count);
        Assert.Equal("bluescreen", crashes[0].Kind);
        Assert.Equal("0x00000116", crashes[0].Code);
        Assert.Equal("VIDEO TDR FAILURE", crashes[0].Name);
        Assert.Equal(@"C:\WINDOWS\Minidump\111026-9406-01.dmp", crashes[0].DumpPath);
        Assert.Equal("power-loss", crashes[1].Kind);
    }

    [Fact]
    public void AnUnknownCodeStillGetsAnAnswer()
    {
        var (_, meaning) = CrashReader.Describe(0xDEAD);
        Assert.Contains("Search for the code", meaning);
    }
}

public class ConnectionLoggerTests : IDisposable
{
    private readonly string _log = Path.Combine(Path.GetTempPath(), $"tf-conn-{Guid.NewGuid():N}.csv");
    private DateTimeOffset _now = new(2026, 11, 20, 21, 0, 0, TimeSpan.Zero);

    public void Dispose() => File.Delete(_log);

    private sealed class NoPing : IPinger
    {
        public int? Ping(IPAddress address, int timeoutMs) => null;
    }

    private ConnectionLogger Logger() => new(new NoPing(), _log, () => _now);

    private void Second(ConnectionLogger logger, int? router, int? internet)
    {
        logger.Record("router", router);
        logger.Record("internet", internet);
        _now = _now.AddSeconds(1);
    }

    [Fact]
    public void LogsAnInternetDropWithItsLength()
    {
        var logger = Logger();
        for (var i = 0; i < 12; i++)
        {
            Second(logger, 1, 20);
        }

        for (var i = 0; i < 5; i++)
        {
            Second(logger, 1, null);
        }

        Second(logger, 1, 20);

        var drop = Assert.Single(logger.Status().Events);
        Assert.Equal(("internet", "drop", 5), (drop.Where, drop.Kind, drop.Seconds));
        Assert.Contains("internet,drop,5", File.ReadAllText(_log));
    }

    [Fact]
    public void ARouterDropIsntCountedTwice()
    {
        var logger = Logger();
        for (var i = 0; i < 12; i++)
        {
            Second(logger, 1, 20);
        }

        for (var i = 0; i < 3; i++)
        {
            Second(logger, null, null);
        }

        Second(logger, 1, 20);

        Assert.Equal("router", Assert.Single(logger.Status().Events).Where);
    }

    [Fact]
    public void FlagsASpikeAgainstTheUsualPing()
    {
        var logger = Logger();
        for (var i = 0; i < 12; i++)
        {
            Second(logger, 1, 20);
        }

        Second(logger, 1, 90);
        Assert.Empty(logger.Status().Events);

        Second(logger, 1, 400);
        var spike = Assert.Single(logger.Status().Events);
        Assert.Equal(("internet", "spike", 400), (spike.Where, spike.Kind, spike.Ms!.Value));
    }
}

public sealed class HostsFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tf-hosts").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string Sample = """
        # Copyright (c) 1993-2009 Microsoft Corp.
        #	127.0.0.1       localhost
        #	::1             localhost

        # Added by Docker Desktop
        192.168.1.20 host.docker.internal
        127.0.0.1 ads.example.com tracker.example.com
        0.0.0.0 telemetry.example.net # blocked by a privacy tool
        not an entry
        """;

    [Fact]
    public void ReadsEntriesWithTheirNotes()
    {
        var entries = HostsFile.Parse(Sample);

        Assert.Equal(3, entries.Count);
        Assert.Equal("Added by Docker Desktop", entries[0].Note);
        Assert.False(entries[0].Blocks);
        Assert.Equal(["ads.example.com", "tracker.example.com"], entries[1].Names);
        Assert.True(entries[1].Blocks);
        Assert.Equal("blocked by a privacy tool", entries[2].Note);
    }

    [Fact]
    public void RemovesOnlyExactLinesAndCanUndo()
    {
        var path = Path.Combine(_root, "hosts");
        File.WriteAllText(path, Sample.Replace("\r\n", "\n"));
        var hosts = new HostsFile(path, Path.Combine(_root, "backup"));

        Assert.True(hosts.Remove(["127.0.0.1 ads.example.com tracker.example.com"]).Ok);
        Assert.DoesNotContain("ads.example.com", File.ReadAllText(path));
        Assert.Contains("host.docker.internal", File.ReadAllText(path));
        Assert.True(hosts.Read().HasBackup);

        Assert.True(hosts.Restore().Ok);
        Assert.Contains("ads.example.com", File.ReadAllText(path));
        Assert.False(hosts.Read().HasBackup);
    }

    [Fact]
    public void RefusesLinesThatArentEntries()
    {
        var path = Path.Combine(_root, "hosts");
        File.WriteAllText(path, Sample);
        var hosts = new HostsFile(path, Path.Combine(_root, "backup"));

        Assert.Throws<CatalogRequestException>(() => hosts.Remove(["# Copyright (c) 1993-2009 Microsoft Corp."]));
    }
}

public sealed class DuplicateFinderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tf-dupes").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Make(string relative, byte fill, long size = DuplicateFinder.MinBytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[size];
        Array.Fill(bytes, fill);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void FindsIdenticalFilesOnly()
    {
        Make("a.iso", 1);
        Make(Path.Combine("copies", "a (1).iso"), 1);
        Make("b.iso", 2);
        Make(Path.Combine("steamapps", "game.pak"), 1);
        Make("small.txt", 1, 100);

        var report = DuplicateFinder.Scan([_root]);

        var group = Assert.Single(report.Groups);
        Assert.Equal(2, group.Files.Count);
        Assert.Equal(DuplicateFinder.MinBytes, report.WastedBytes);
    }

    [Fact]
    public void NeverRemovesTheLastCopy()
    {
        var first = Make("a.iso", 1);
        var second = Make("a (1).iso", 1);
        var different = Make("c.iso", 3);
        var removed = new List<string>();
        bool Recycle(string path)
        {
            removed.Add(path);
            File.Delete(path);
            return true;
        }

        var result = DuplicateFinder.Recycle(
            [new DuplicatePick(second, first), new DuplicatePick(first, second), new DuplicatePick(different, first)],
            [_root],
            Recycle);

        Assert.Equal(new RecycleResult(1, 2), result);
        Assert.Equal([second], removed);
        Assert.True(File.Exists(first));
    }
}

public sealed class LeftoverFinderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tf-left").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("Discord", "discord")]
    [InlineData("OBS Studio 31.0.2 (64-bit)", "obsstudio")]
    [InlineData("WinRAR 7.01 (x64)", "winrar")]
    public void MakesAComparableName(string name, string key) => Assert.Equal(key, LeftoverFinder.Key(name));

    [Fact]
    public void FindsMatchingFoldersAndMarksWhichCanGo()
    {
        var roaming = Path.Combine(_root, "Roaming");
        var programs = Path.Combine(_root, "Program Files");
        Directory.CreateDirectory(Path.Combine(roaming, "obs-studio"));
        Directory.CreateDirectory(Path.Combine(roaming, "Maker", "OBS Studio"));
        Directory.CreateDirectory(Path.Combine(roaming, "OBS Helper"));
        Directory.CreateDirectory(Path.Combine(programs, "OBS Studio"));

        var found = LeftoverFinder.Find("OBS Studio 31.0.2", [(roaming, true), (programs, false)], _ => 10);

        Assert.Equal(3, found.Count);
        Assert.Equal(2, found.Count(folder => folder.CanRemove));
        Assert.DoesNotContain(found, folder => folder.Path.EndsWith("OBS Helper", StringComparison.Ordinal));
    }

    [Fact]
    public void IgnoresNamesThatAreTooGeneral()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Microsoft"));
        Assert.Empty(LeftoverFinder.Find("Microsoft", [(_root, true)], _ => 0));
        Assert.Empty(LeftoverFinder.Find("Go", [(_root, true)], _ => 0));
    }
}

public class StartupLaunchTests
{
    [Theory]
    [InlineData("\"C:\\App\\Update.exe\" --processStart Discord.exe", "C:\\App\\Update.exe", "--processStart Discord.exe")]
    [InlineData("C:\\Program Files\\App\\app.exe -tray", "C:\\Program Files\\App\\app.exe", "-tray")]
    [InlineData("C:\\Tools\\tool.exe", "C:\\Tools\\tool.exe", "")]
    public void SplitsTheProgramFromItsArguments(string command, string file, string arguments)
    {
        Assert.Equal((file, arguments), StartupLaunch.Split(command));
    }
}

public sealed class SnapshotTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tf-snap").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static PcSnapshot Snap(string date, string[] programs, string[] startup) => new(date, startup, ["Windows Update"], programs, []);

    [Fact]
    public void ComparesWithAboutAWeekAgo()
    {
        var store = new SnapshotStore(_root);
        store.Save(Snap("2026-11-10", ["Steam"], ["Steam"]), new DateOnly(2026, 11, 10));
        store.Save(Snap("2026-11-12", ["Steam", "Old Tool"], ["Steam"]), new DateOnly(2026, 11, 12));
        store.Save(Snap("2026-11-18", ["Steam", "Old Tool", "Discord"], ["Steam"]), new DateOnly(2026, 11, 18));

        store.Save(Snap("2026-11-19", ["Steam 1.0"], ["Steam"]), new DateOnly(2026, 11, 19));
        var report = store.Compare(Snap("2026-11-20", ["Discord", "Steam", "New Game"], ["Steam", "Discord"]), new DateOnly(2026, 11, 20));

        Assert.Equal("2026-11-12", report.SinceDate);
        var programs = report.Changes.Single(change => change.Category == "Programs");
        Assert.Equal(["Discord", "New Game"], programs.Added);
        Assert.Equal(["Old Tool"], programs.Removed);
        Assert.Equal(["Discord"], report.Changes.Single(change => change.Category == "Startup programs").Added);
    }

    [Fact]
    public void HasNothingToCompareAtFirst()
    {
        Assert.Null(new SnapshotStore(_root).Compare(Snap("2026-11-20", [], []), new DateOnly(2026, 11, 20)).SinceDate);
    }

    [Fact]
    public void NamesDriversByMakerAndType()
    {
        const string inf = """
            [Version]
            Class     = Display
            Provider  = %NVIDIA%
            DriverVer = 10/01/2025,32.0.15.8180

            [Strings]
            NVIDIA = "NVIDIA"
            """;
        Assert.Equal("NVIDIA Display 32.0.15.8180", SnapshotStore.DriverName(inf));
    }
}

public sealed class HealthHistoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tf-health-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void KeepsOneScorePerDay()
    {
        var history = new HealthHistory(_path);
        history.Record(80, new DateOnly(2026, 11, 19));
        history.Record(70, new DateOnly(2026, 11, 20));
        var points = history.Record(75, new DateOnly(2026, 11, 20));

        Assert.Equal([new HealthPoint("2026-11-19", 80), new HealthPoint("2026-11-20", 75)], points);
        Assert.Equal(points, new HealthHistory(_path).Read());
    }
}

public class SmallHelperTests
{
    [Theory]
    [InlineData(2412, 1)]
    [InlineData(2437, 6)]
    [InlineData(2484, 14)]
    [InlineData(5180, 36)]
    [InlineData(5955, 1)]
    public void WorksOutTheWifiChannel(int megahertz, int channel) => Assert.Equal(channel, WifiReader.ChannelOf(megahertz));

    [Fact]
    public void SignalMatchesWindowsBars()
    {
        Assert.Equal(100, WifiReader.SignalPercent(-45));
        Assert.Equal(50, WifiReader.SignalPercent(-75));
        Assert.Equal(0, WifiReader.SignalPercent(-110));
    }

    [Theory]
    [InlineData("Microsoft.WindowsStore_8wekyb3d8bbwe", "Windows Store")]
    [InlineData("28017CharlesMilette.TranslucentTB_v826wp6bftszj", "Translucent TB")]
    [InlineData("Microsoft.Ink.Handwriting.Main.en-US.1.0.1_8wekyb3d8bbwe", "Handwriting Main")]
    [InlineData("MicrosoftCorporationII.WinAppRuntime.Main.2_8wekyb3d8bbwe", "Win App Runtime Main")]
    [InlineData("5319275A.51895FA4EA97F_cv1g1gvanyjgm", "5319275A.51895FA4EA97F")]
    public void NamesStoreApps(string family, string name) => Assert.Equal(name, AppNames.FromPackage(family));

    [Fact]
    public void NamesDesktopApps() =>
        Assert.Equal("chrome", AppNames.FromPath("C:#Program Files#Google#Chrome#Application#chrome.exe"));

    [Theory]
    [InlineData("Hyper-V Virtual Ethernet Adapter", false)]
    [InlineData("ExitLag Tap Adapter", false)]
    [InlineData("Realtek PCIe 2.5GbE Family Controller", true)]
    [InlineData("Ethernet-WFP Native MAC Layer LightWeight Filter-0000", false)]
    [InlineData("Ethernet-QoS Packet Scheduler-0000", false)]
    public void SkipsVirtualAdapters(string description, bool physical) => Assert.Equal(physical, LinkSpeed.IsPhysical(description));

    [Fact]
    public void IconCacheOnlyListsCacheFiles()
    {
        var root = Directory.CreateTempSubdirectory("tf-icons").FullName;
        try
        {
            var explorer = Path.Combine(root, "Microsoft", "Windows", "Explorer");
            Directory.CreateDirectory(explorer);
            File.WriteAllText(Path.Combine(root, "IconCache.db"), "");
            File.WriteAllText(Path.Combine(explorer, "iconcache_32.db"), "");
            File.WriteAllText(Path.Combine(explorer, "thumbcache_256.db"), "");
            File.WriteAllText(Path.Combine(explorer, "ExplorerStartupLog.etl"), "");

            Assert.Equal(3, IconCache.CacheFiles(root).Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
