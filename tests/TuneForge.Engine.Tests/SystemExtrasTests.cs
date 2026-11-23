using TuneForge.Contracts;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

public class SystemExtrasTests
{
    [Fact]
    public void ReadsWhatKeepsThePcAwake()
    {
        const string output = """
            DISPLAY:
            None.

            SYSTEM:
            [DRIVER] Realtek High Definition Audio (HDAUDIO\FUNC_01&VEN_10EC)
            An audio stream is currently in use.

            AWAYMODE:
            None.

            EXECUTION:
            [PROCESS] \Device\HarddiskVolume3\Users\me\AppData\Roaming\Spotify\Spotify.exe
            Playing audio

            PERFBOOST:
            [PROCESS] \Device\HarddiskVolume3\Windows\something.exe

            ACTIVELOCKSCREEN:
            None.
            """;

        var blockers = WakeTools.ParseRequests(output);

        Assert.Equal(2, blockers.Count);
        Assert.Equal(new SleepBlocker("system", "Realtek High Definition Audio (HDAUDIO\\FUNC_01&VEN_10EC)", "An audio stream is currently in use."), blockers[0]);
        Assert.Equal(new SleepBlocker("system", "Spotify.exe", "Playing audio"), blockers[1]);
    }

    [Fact]
    public void NothingKeepingItAwakeIsAnEmptyList()
    {
        Assert.Empty(WakeTools.ParseRequests("DISPLAY:\nNone.\n\nSYSTEM:\nNone.\n"));
    }

    [Fact]
    public void NamesTheWakeSource()
    {
        const string withText = """<Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><EventData><Data Name="WakeSourceType">5</Data><Data Name="WakeSourceText">HID-compliant mouse</Data></EventData></Event>""";
        const string withoutText = """<Event><EventData><Data Name="WakeSourceType">1</Data><Data Name="WakeSourceText"></Data></EventData></Event>""";

        Assert.Equal("HID-compliant mouse", WakeTools.WakeSource(withText));
        Assert.Equal("Power button", WakeTools.WakeSource(withoutText));
    }

    [Theory]
    [InlineData("Riot Client Updater", "C:\\Riot Games\\Riot Client\\RiotClientServices.exe", true)]
    [InlineData("EAC check", "C:\\Program Files\\EasyAntiCheat\\EasyAntiCheat.exe", true)]
    [InlineData("TuneForge Update", "C:\\Program Files\\TuneForge\\TuneForge.exe", true)]
    [InlineData("GoogleUpdateTaskMachineUA", "C:\\Program Files\\Google\\Update\\GoogleUpdate.exe", false)]
    public void LocksTasksGamesNeed(string name, string program, bool locked) => Assert.Equal(locked, ScheduledTasks.IsLocked(name, program));

    [Theory]
    [InlineData("game.exe")]
    [InlineData("C:\\Games\\it's.exe")]
    [InlineData("C:\\Games\\launcher.txt")]
    [InlineData("C:\\Windows\\System32\\svchost.exe")]
    [InlineData("C:\\Riot Games\\VALORANT\\live\\VALORANT.exe")]
    [InlineData("C:\\Program Files\\TuneForge\\TuneForge.exe")]
    public void RefusesProgramsThatMustNotBeBlocked(string program)
    {
        Assert.Throws<CatalogRequestException>(() => FirewallBlocks.Check(program));
    }

    [Fact]
    public void AcceptsAnOrdinaryProgram() => FirewallBlocks.Check("C:\\Program Files\\Some App\\updater.exe");

    [Theory]
    [InlineData(4, 17, "NVMe SSD")]
    [InlineData(4, 11, "SSD")]
    [InlineData(3, 11, "Hard drive")]
    [InlineData(0, 7, "Drive")]
    public void NamesTheKindOfDrive(int media, int bus, string name) => Assert.Equal(name, DriveHealth.MediaName(media, bus));

    [Theory]
    [InlineData("RiotClientCrashHandler", true)]
    [InlineData("vgtray", true)]
    [InlineData("Discord", false)]
    public void StaysAwayFromAntiCheat(string name, bool skipped) => Assert.Equal(skipped, SignatureCheck.NearAntiCheat(name));

    [Fact]
    public void SavesDriversUnderDocuments()
    {
        var backup = new AllDriversBackup(new FakeRunner(), @"C:\Users\me", 26100);
        Assert.Equal(@"C:\Users\me\Documents\TuneForge Backups\Drivers 2026-11-23 14-05", backup.FolderFor(new DateTime(2026, 11, 23, 14, 5, 0)));
    }

    [Fact]
    public void RefusesAFolderWithoutDrivers()
    {
        var empty = Directory.CreateTempSubdirectory("tf-nodrivers").FullName;
        try
        {
            var runner = new FakeRunner();
            Assert.Throws<CatalogRequestException>(() => new AllDriversBackup(runner, @"C:\Users\me", 26100).Import(empty));
            Assert.Empty(runner.Commands);
        }
        finally
        {
            Directory.Delete(empty);
        }
    }

    [Fact]
    public void InstallsFromAFolderWithDrivers()
    {
        var folder = Directory.CreateTempSubdirectory("tf-drivers").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "oem12"));
            File.WriteAllText(Path.Combine(folder, "oem12", "nv.inf"), "");
            var runner = new FakeRunner(3010);

            var result = new AllDriversBackup(runner, @"C:\Users\me", 26100).Import(folder);

            Assert.True(result.Ok);
            Assert.True(result.RestartNeeded);
            Assert.Equal($"pnputil.exe /add-driver {Path.Combine(folder, "*.inf")} /subdirs /install", Assert.Single(runner.Commands));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void OldWindowsCantExport()
    {
        var runner = new FakeRunner();
        Assert.False(new AllDriversBackup(runner, @"C:\Users\me", 18363).Export(DateTime.Now).Ok);
        Assert.Empty(runner.Commands);
    }
}
