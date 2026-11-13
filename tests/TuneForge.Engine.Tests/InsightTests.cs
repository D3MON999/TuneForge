using TuneForge.Contracts;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

public sealed class SpaceFinderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tf-space").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string File(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void CountsEverythingInsideEachFolderAndSortsBiggestFirst()
    {
        File(@"games\a\big.bin", 5000);
        File(@"games\b\more.bin", 3000);
        File(@"docs\note.txt", 100);
        File("loose.bin", 2000);

        var report = SpaceFinder.Scan(_root);

        Assert.Equal(10100, report.Bytes);
        Assert.Equal(["games", "loose.bin", "docs"], report.Items.Select(item => item.Name));
        Assert.Equal(8000, report.Items[0].Bytes);
        Assert.True(report.Items[0].IsFolder);
        Assert.False(report.Items[1].IsFolder);
    }

    [Fact]
    public void ListsTheLargestFilesAnywhereInside()
    {
        File(@"deep\down\here\huge.bin", 9000);
        File(@"top.bin", 10);

        var report = SpaceFinder.Scan(_root);

        Assert.Equal("huge.bin", report.LargestFiles[0].Name);
        Assert.EndsWith(@"deep\down\here\huge.bin", report.LargestFiles[0].Path);
    }

    [Fact]
    public void KeepsOnlyTheTwentyLargestFiles()
    {
        for (var index = 1; index <= 30; index++)
        {
            File($"f{index}.bin", index);
        }

        var largest = SpaceFinder.Scan(_root).LargestFiles;

        Assert.Equal(20, largest.Count);
        Assert.Equal(30, largest[0].Bytes);
        Assert.Equal(11, largest[^1].Bytes);
    }

    [Fact]
    public void DoesNotFollowLinksOutOfTheFolder()
    {
        var outside = Directory.CreateTempSubdirectory("tf-outside").FullName;
        try
        {
            System.IO.File.WriteAllBytes(Path.Combine(outside, "elsewhere.bin"), new byte[50_000]);
            Directory.CreateSymbolicLink(Path.Combine(_root, "link"), outside);
        }
        catch (IOException)
        {
            Directory.Delete(outside, true);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            Directory.Delete(outside, true);
            return;
        }

        var report = SpaceFinder.Scan(_root);

        Assert.Equal(0, report.Bytes);
        Directory.Delete(outside, true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative\\path")]
    [InlineData(@"C:\this\does\not\exist\tuneforge")]
    public void RefusesBadFolders(string path)
    {
        Assert.Throws<CatalogRequestException>(() => SpaceFinder.Scan(path));
    }

    [Fact]
    public void StopsWhenCancelled()
    {
        File(@"a\x.bin", 10);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(() => SpaceFinder.Scan(_root, cancel.Token));
    }
}

public class GraphicsDriverTests
{
    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2503", "NVIDIA")]
    [InlineData(@"PCI\VEN_1002&DEV_73BF", "AMD")]
    [InlineData(@"PCI\VEN_8086&DEV_A780", "Intel")]
    [InlineData(@"ROOT\DISPLAY\0000", "Other")]
    public void KnowsTheMaker(string pnp, string vendor)
    {
        Assert.Equal(vendor, GraphicsDrivers.VendorOf(pnp));
    }

    [Theory]
    [InlineData("32.0.16.1714", "617.14")]
    [InlineData("31.0.15.5222", "552.22")]
    public void TurnsTheWindowsNumberIntoNvidias(string version, string friendly)
    {
        Assert.Equal(friendly, GraphicsDrivers.Friendly("NVIDIA", version));
    }

    [Fact]
    public void OnlyNvidiaGetsAFriendlyNumber()
    {
        Assert.Null(GraphicsDrivers.Friendly("AMD", "31.0.21001.45002"));
    }

    [Fact]
    public void WorksOutTheAge()
    {
        var driver = GraphicsDrivers.Describe("RTX", @"PCI\VEN_10DE&DEV_1", "32.0.16.1714", new DateTime(2026, 9, 17), new DateTime(2026, 11, 7));

        Assert.Equal(51, driver.AgeDays);
        Assert.True(driver.HasDownloadPage);
    }

    [Theory]
    [InlineData("NVIDIA", "https://www.nvidia.com/")]
    [InlineData("AMD", "https://www.amd.com/")]
    [InlineData("Intel", "https://www.intel.com/")]
    public void DownloadPagesAreTheMakersOwnSites(string vendor, string start)
    {
        Assert.StartsWith(start, GraphicsDrivers.DownloadPage(vendor));
    }

    [Fact]
    public void UnknownMakersHaveNoPage()
    {
        Assert.Null(GraphicsDrivers.DownloadPage("Other"));
        Assert.Null(GraphicsDrivers.DownloadPage("https://evil.example"));
    }
}

public class BootHistoryTests
{
    private const string Sample = """
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <EventData>
            <Data Name="BootTime">31250</Data>
            <Data Name="MainPathBootTime">12400</Data>
            <Data Name="BootPostBootTime">18850</Data>
          </EventData>
        </Event>
        """;

    [Fact]
    public void ReadsWindowsOwnTimings()
    {
        var boot = BootHistory.Parse(Sample, new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc))!;

        Assert.Equal(31250, boot.TotalMs);
        Assert.Equal(12400, boot.MainPathMs);
        Assert.Equal(18850, boot.AfterSignInMs);
        Assert.StartsWith("2026-11-07T10:00:00", boot.WhenUtc);
    }

    [Fact]
    public void SkipsRecordsWithoutATime()
    {
        Assert.Null(BootHistory.Parse("<Event><EventData><Data Name=\"BootTime\">0</Data></EventData></Event>", DateTime.UtcNow));
        Assert.Null(BootHistory.Parse("<Event><EventData /></Event>", DateTime.UtcNow));
    }
}

public class SlowStartupAppTests
{
    private static string Record(string name, string friendly, int added) => $"""
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <EventData>
            <Data Name="Name">{name}</Data>
            <Data Name="FriendlyName">{friendly}</Data>
            <Data Name="TotalTime">{added + 900}</Data>
            <Data Name="DegradationTime">{added}</Data>
          </EventData>
        </Event>
        """;

    [Fact]
    public void ReadsTheAppAndItsDelay()
    {
        var app = BootHistory.ParseSlowApp(Record("OneDrive.exe", "Microsoft OneDrive", 1800))!.Value;

        Assert.Equal("Microsoft OneDrive", app.Name);
        Assert.Equal("OneDrive.exe", app.File);
        Assert.Equal(1800, app.AddedMs);
    }

    [Fact]
    public void SkipsRecordsWithoutADelay()
    {
        Assert.Null(BootHistory.ParseSlowApp(Record("app.exe", "App", 0)));
        Assert.Null(BootHistory.ParseSlowApp("<Event><EventData /></Event>"));
    }

    [Fact]
    public void PutsTheWorstOffenderFirst()
    {
        var apps = BootHistory.Summarize([("Chat", "chat.exe", 1000), ("Chat", "CHAT.EXE", 3000), ("Sync", "sync.exe", 2500)]);

        Assert.Equal(["chat.exe", "sync.exe"], apps.Select(app => app.File));
        Assert.Equal(2, apps[0].Times);
        Assert.Equal(2000, apps[0].AddedMs);
    }
}

public class BatteryHealthTests
{
    [Fact]
    public void ReadsCapacitiesFromWindowsReport()
    {
        const string report = """
            <?xml version="1.0" encoding="utf-8"?>
            <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
              <Batteries>
                <Battery>
                  <Id>DELL 1234</Id>
                  <Manufacturer>SMP</Manufacturer>
                  <DesignCapacity>54000</DesignCapacity>
                  <FullChargeCapacity>43200</FullChargeCapacity>
                  <CycleCount>287</CycleCount>
                </Battery>
              </Batteries>
            </BatteryReport>
            """;

        var battery = Assert.Single(BatteryHealth.Parse(report).Batteries);
        Assert.Equal("DELL 1234", battery.Name);
        Assert.Equal(54000, battery.DesignMwh);
        Assert.Equal(43200, battery.FullMwh);
        Assert.Equal(287, battery.Cycles);
    }

    [Fact]
    public void ADesktopHasNoBatteries()
    {
        Assert.Empty(BatteryHealth.Parse("<BatteryReport><Batteries /></BatteryReport>").Batteries);
    }

    [Fact]
    public void AMissingCycleCountIsUnknown()
    {
        var report = BatteryHealth.Parse("<BatteryReport><Batteries><Battery><DesignCapacity>40000</DesignCapacity><FullChargeCapacity>39000</FullChargeCapacity><CycleCount>0</CycleCount></Battery></Batteries></BatteryReport>");
        Assert.Null(Assert.Single(report.Batteries).Cycles);
    }
}

public sealed class OldDownloadsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tf-downloads").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string MakeFile(string name, long bytes, DateTime touchedUtc)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = File.Create(path))
        {
            stream.SetLength(bytes);
        }

        File.SetCreationTimeUtc(path, touchedUtc);
        File.SetLastWriteTimeUtc(path, touchedUtc);
        return path;
    }

    [Fact]
    public void ListsOnlyBigFilesLeftAlone()
    {
        var now = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
        MakeFile("old-big.iso", OldDownloads.MinBytes * 2, now.AddDays(-200));
        MakeFile(Path.Combine("setups", "old-installer.exe"), OldDownloads.MinBytes, now.AddDays(-120));
        MakeFile("new-big.zip", OldDownloads.MinBytes * 3, now.AddDays(-10));
        MakeFile("old-small.pdf", 1024, now.AddDays(-400));

        var report = OldDownloads.Scan(_root, now);

        Assert.Equal(["old-big.iso", "old-installer.exe"], report.Files.Select(file => file.Name));
        Assert.Equal(200, report.Files[0].AgeDays);
    }

    [Fact]
    public void AMissingFolderListsNothing()
    {
        Assert.Empty(OldDownloads.Scan(Path.Combine(_root, "missing"), DateTime.UtcNow).Files);
    }

    [Theory]
    [InlineData(@"C:\Users\me\Downloads\movie.mkv", true)]
    [InlineData(@"C:\Users\me\Downloads\sub\a..b.zip", true)]
    [InlineData(@"C:\Users\me\Downloads", false)]
    [InlineData(@"C:\Users\me\Downloads\..\Documents\taxes.pdf", false)]
    [InlineData(@"C:\Users\me\DownloadsOther\file.zip", false)]
    [InlineData(@"C:\Windows\System32\kernel32.dll", false)]
    [InlineData(@"movie.mkv", false)]
    [InlineData("", false)]
    public void OnlyFilesInsideDownloadsCanBeRemoved(string path, bool allowed)
    {
        Assert.Equal(allowed, OldDownloads.IsInside(@"C:\Users\me\Downloads", path));
    }
}

public class TrayMessageTests
{
    [Fact]
    public void SaysWhenAntiCheatBlocksIt()
    {
        Assert.Contains("anti-cheat", TrayMessages.Memory(new MemoryResult(0, 0, 0, 0, Skipped: true)));
    }

    [Theory]
    [InlineData(500L * 1024 * 1024, "Freed 500 MB.")]
    [InlineData(2L * 1024 * 1024 * 1024, "Freed 2.0 GB.")]
    [InlineData(100, "Memory is already in good shape.")]
    public void ReportsWhatWasFreed(long freed, string message)
    {
        Assert.Equal(message, TrayMessages.Memory(new MemoryResult(freed + 1000, 1000, 1L << 34, 10)));
    }
}
