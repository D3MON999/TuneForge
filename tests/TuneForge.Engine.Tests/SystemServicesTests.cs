using System.Diagnostics;
using TuneForge.Contracts;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

internal sealed class FakeRunner(params int[] codes) : IProcessRunner
{
    private int _next;

    public List<string> Commands { get; } = [];

    public int Run(string tool, IReadOnlyList<string> arguments, TimeSpan timeLimit)
    {
        Commands.Add($"{tool} {string.Join(' ', arguments)}");
        return _next < codes.Length ? codes[_next++] : 0;
    }
}

internal sealed class FakeBin(long bytes = 0) : IRecycleBin
{
    public bool Emptied { get; private set; }

    public (long Bytes, long Items) Query() => (bytes, bytes == 0 ? 0 : 1);

    public void Empty() => Emptied = true;
}

public sealed class CleanupServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tuneforge-cleanup").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("tuneforge-outside").FullName;

    private string Junk => Path.Combine(_root, "junk");

    private string Write(string relative, int size = 100, TimeSpan? age = null, string? under = null)
    {
        var path = Path.Combine(under ?? Junk, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - (age ?? TimeSpan.FromDays(10)));
        return path;
    }

    private CleanupService Service(TimeSpan? minimumAge = null, string pattern = "*", bool admin = false, IRecycleBin? bin = null) => new(
        [
            new CleanupDefinition("junk", "Junk", "n", admin, true, [Junk], pattern, minimumAge),
            new CleanupDefinition("bin", "Bin", "n", false, false, [], IsRecycleBin: true),
        ],
        bin ?? new FakeBin());

    [Fact]
    public void Scan_MeasuresWithoutDeleting()
    {
        var file = Write("a.tmp");
        Write(@"deep\b.tmp", 50);

        var info = Service().Scan().Single(item => item.Id == "junk");

        Assert.Equal(150, info.Bytes);
        Assert.Equal(2, info.Files);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Clean_RemovesFilesAndEmptyFolders_ButKeepsTheRoot()
    {
        Write(@"deep\er\a.tmp");

        var result = Service().Clean(["junk"], elevated: false);

        Assert.Equal(100, result.FreedBytes);
        Assert.Equal(1, result.Files);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Junk));
        Assert.True(Directory.Exists(Junk));
    }

    [Fact]
    public void Clean_KeepsRecentFiles()
    {
        var fresh = Write("new.tmp", age: TimeSpan.FromMinutes(5));
        var old = Write("old.tmp", age: TimeSpan.FromDays(2));

        Service(TimeSpan.FromDays(1)).Clean(["junk"], elevated: false);

        Assert.True(File.Exists(fresh));
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void Clean_OnlyTakesMatchingFiles()
    {
        var keep = Write("notes.txt");
        var remove = Write("thumbcache_32.db");

        Service(pattern: "thumbcache_*.db").Clean(["junk"], elevated: false);

        Assert.True(File.Exists(keep));
        Assert.False(File.Exists(remove));
    }

    [Fact]
    public void Clean_NeverFollowsLinksOutOfTheFolder()
    {
        var precious = Write("precious.doc", under: _outside);
        Write("a.tmp");
        var link = Path.Combine(Junk, "link");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{_outside}\"")
               {
                   UseShellExecute = false,
                   CreateNoWindow = true,
                   RedirectStandardOutput = true,
               })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }

        var info = Service().Scan().Single(item => item.Id == "junk");
        Service().Clean(["junk"], elevated: false);

        Assert.Equal(1, info.Files);
        Assert.True(File.Exists(precious));

        // Windows only lets this unprivileged test remove a junction through rmdir.
        using var rmdir = Process.Start(new ProcessStartInfo("cmd.exe", $"/c rmdir \"{link}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        rmdir.WaitForExit();
    }

    [Fact]
    public void Clean_SkipsFilesInUse()
    {
        var path = Write("locked.tmp");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = Service().Clean(["junk"], elevated: false);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Files);
    }

    [Fact]
    public void Clean_RefusesUnknownCategories() =>
        Assert.Throws<CatalogRequestException>(() => Service().Clean(["windows-folder"], elevated: true));

    [Fact]
    public void Clean_RefusesAdminCategoriesWithoutAdmin()
    {
        var file = Write("a.tmp");

        Assert.Throws<CatalogRequestException>(() => Service(admin: true).Clean(["junk"], elevated: false));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void RecycleBin_IsEmptiedOnlyWhenAsked()
    {
        var bin = new FakeBin(500);

        Service(bin: bin).Clean(["junk"], elevated: false);
        Assert.False(bin.Emptied);

        var result = Service(bin: bin).Clean(["bin"], elevated: false);
        Assert.True(bin.Emptied);
        Assert.Equal(500, result.FreedBytes);
    }

    [Fact]
    public void Standard_NeverOffersPermanentDeletionByDefault()
    {
        var bin = CleanupService.Standard(new UserPaths(_root)).Single(item => item.IsRecycleBin);

        Assert.False(bin.DefaultOn);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }
}

public sealed class StartupServiceTests : IDisposable
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private readonly string _profile = Directory.CreateTempSubdirectory("tuneforge-profile").FullName;
    private readonly FakeRegistry _registry = new();

    private StartupService Service => new(_registry, new UserPaths(_profile));

    [Fact]
    public void List_ReadsRunKeysAndApprovedState()
    {
        _registry.Write("HKCU", Run, "On", new RegistryValue("string", "on.exe"));
        _registry.Write("HKCU", Run, "Off", new RegistryValue("string", "off.exe"));
        _registry.Write("HKCU", Approved, "Off", new RegistryValue("binary", "030000000000000000000000"));

        var entries = Service.List();

        Assert.True(entries.Single(entry => entry.Name == "On").Enabled);
        Assert.False(entries.Single(entry => entry.Name == "Off").Enabled);
        Assert.Equal("on.exe", entries.Single(entry => entry.Name == "On").Command);
    }

    [Fact]
    public void List_IncludesStartupFolderFiles_ButNotDesktopIni()
    {
        var folder = Path.Combine(_profile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "App.lnk"), string.Empty);
        File.WriteAllText(Path.Combine(folder, "desktop.ini"), string.Empty);

        Assert.Equal(["App.lnk"], Service.List().Where(entry => entry.Source == "user-folder").Select(entry => entry.Name));
    }

    [Fact]
    public void BuildChange_WritesTheApprovedValue_AndNeverTheRunEntry()
    {
        _registry.Write("HKCU", Run, "App", new RegistryValue("string", "app.exe"));

        var item = Service.BuildChange(new StartupSetRequest("hkcu-run", "App", false));

        var action = Assert.Single(item.Actions);
        Assert.Equal(Approved, action.Path);
        Assert.Equal("binary", action.Target!.Kind);
        Assert.StartsWith("03", action.Target.Data);
    }

    [Theory]
    [InlineData("hkcu-run", "Missing")]
    [InlineData("unknown-source", "App")]
    public void BuildChange_RefusesThingsThatDoNotExist(string source, string name)
    {
        _registry.Write("HKCU", Run, "App", new RegistryValue("string", "app.exe"));

        Assert.Throws<CatalogRequestException>(() => Service.BuildChange(new StartupSetRequest(source, name, false)));
    }

    public void Dispose() => Directory.Delete(_profile, recursive: true);
}

public class ServiceManagerTests
{
    private const string Root = @"SYSTEM\CurrentControlSet\Services\";

    private sealed class Source(params string[] names) : IServiceSource
    {
        public IReadOnlyList<ServiceSnapshot> List() => names.Select(name => new ServiceSnapshot(name, name, false)).ToList();
    }

    private static ServiceManager Manager(FakeRegistry registry, params (string Name, int Start)[] services)
    {
        foreach (var (name, start) in services)
        {
            registry.Write("HKLM", Root + name, "Start", new RegistryValue("dword", start.ToString()));
        }

        return new ServiceManager(registry, new Source(services.Select(service => service.Name).ToArray()));
    }

    [Theory]
    [InlineData("WinDefend")]
    [InlineData("mpssvc")]
    [InlineData("RpcSs")]
    public void EssentialAndSecurityServices_AreLocked(string name)
    {
        var manager = Manager(new FakeRegistry(), (name, 2));

        Assert.True(manager.List().Single().Protected);
        Assert.Throws<CatalogRequestException>(() => manager.BuildChange(new ServiceSetRequest(name, "disabled")));
    }

    [Fact]
    public void BootDrivers_AreLocked()
    {
        var manager = Manager(new FakeRegistry(), ("disk", 0));

        Assert.Throws<CatalogRequestException>(() => manager.BuildChange(new ServiceSetRequest("disk", "disabled")));
    }

    [Theory]
    [InlineData("auto", "2")]
    [InlineData("manual", "3")]
    [InlineData("disabled", "4")]
    public void BuildChange_SetsTheStartValue(string startType, string expected)
    {
        var manager = Manager(new FakeRegistry(), ("Fax", 3));

        var action = Assert.Single(manager.BuildChange(new ServiceSetRequest("Fax", startType)).Actions);

        Assert.Equal(expected, action.Target!.Data);
        Assert.Equal("dword", action.Target.Kind);
    }

    [Theory]
    [InlineData("Missing", "manual")]
    [InlineData(@"..\Evil", "manual")]
    [InlineData("Fax", "boot")]
    public void BuildChange_RefusesBadRequests(string name, string startType)
    {
        var manager = Manager(new FakeRegistry(), ("Fax", 3));

        Assert.Throws<CatalogRequestException>(() => manager.BuildChange(new ServiceSetRequest(name, startType)));
    }
}

public class FeatureServiceTests
{
    private sealed class Reader(params (string Name, bool On)[] states) : IFeatureStateReader
    {
        public IReadOnlyDictionary<string, bool> Read() => states.ToDictionary(state => state.Name, state => state.On);
    }

    private static FeatureService Service(FakeRunner runner, params (string, bool)[] states) => new(runner, new Reader(states));

    [Fact]
    public void List_ReportsStateAndMissingFeatures()
    {
        var entries = Service(new FakeRunner(), ("Containers-DisposableClientVM", true), ("NetFx3", false)).List();

        Assert.Equal("enabled", entries.Single(entry => entry.Id == "sandbox").State);
        Assert.Equal("disabled", entries.Single(entry => entry.Id == "dotnet").State);
        Assert.Equal("unavailable", entries.Single(entry => entry.Id == "hyper-v").State);
    }

    [Fact]
    public void Set_EnablesEveryPartAndReportsRestart()
    {
        var runner = new FakeRunner(0, 3010);

        var result = Service(runner).Set(new FeatureSetRequest("wsl", true));

        Assert.True(result.Ok);
        Assert.True(result.RestartNeeded);
        Assert.Equal(2, runner.Commands.Count);
        Assert.All(runner.Commands, command => Assert.Contains("/Enable-Feature", command));
    }

    [Fact]
    public void Set_StopsAtTheFirstFailure()
    {
        var runner = new FakeRunner(5, 0);

        var result = Service(runner).Set(new FeatureSetRequest("wsl", true));

        Assert.False(result.Ok);
        Assert.Single(runner.Commands);
    }

    [Fact]
    public void Set_RefusesFeaturesNotOnTheList() =>
        Assert.Throws<CatalogRequestException>(() => Service(new FakeRunner()).Set(new FeatureSetRequest("Windows-Defender", false)));
}

public class RepairServiceTests
{
    private sealed class FakeMover(bool fail = false) : IDirectoryMover
    {
        public List<string> Moves { get; } = [];

        public void Move(string from, string to)
        {
            if (fail)
            {
                throw new IOException("in use");
            }

            Moves.Add(Path.GetFileName(from));
        }
    }

    [Fact]
    public void EveryListedRepair_Runs()
    {
        foreach (var repair in RepairService.Available)
        {
            Assert.True(new RepairService(new FakeRunner(), new FakeMover()).Run(new RepairRequest(repair.Id)).Ok);
        }
    }

    [Fact]
    public void Unknown_IsRefused() =>
        Assert.Throws<CatalogRequestException>(() => new RepairService(new FakeRunner(), new FakeMover()).Run(new RepairRequest("format")));

    [Fact]
    public void SystemFiles_RunsSfc()
    {
        var runner = new FakeRunner();

        new RepairService(runner, new FakeMover()).Run(new RepairRequest("system-files"));

        Assert.Equal(["sfc.exe /scannow"], runner.Commands);
    }

    [Fact]
    public void FailedRepair_SaysSo()
    {
        var result = new RepairService(new FakeRunner(1), new FakeMover()).Run(new RepairRequest("windows-image"));

        Assert.False(result.Ok);
    }

    [Fact]
    public void WindowsUpdate_StopsServicesFirst_AndRenamesInsteadOfDeleting()
    {
        var runner = new FakeRunner();
        var mover = new FakeMover();

        var result = new RepairService(runner, mover).Run(new RepairRequest("windows-update"));

        Assert.True(result.Ok);
        Assert.Equal(["SoftwareDistribution", "catroot2"], mover.Moves);
        Assert.StartsWith("net.exe stop", runner.Commands[0]);
        Assert.StartsWith("net.exe start", runner.Commands[^1]);
    }

    [Fact]
    public void WindowsUpdate_RestartsServicesEvenWhenTheResetFails()
    {
        var runner = new FakeRunner();

        var result = new RepairService(runner, new FakeMover(fail: true)).Run(new RepairRequest("windows-update"));

        Assert.False(result.Ok);
        Assert.Equal(4, runner.Commands.Count(command => command.StartsWith("net.exe start", StringComparison.Ordinal)));
    }

    [Fact]
    public void Network_NeedsARestart()
    {
        var result = new RepairService(new FakeRunner(), new FakeMover()).Run(new RepairRequest("network"));

        Assert.True(result.RestartNeeded);
    }

    [Fact]
    public void Network_TreatsNetshRestartCodeAsSuccess()
    {
        // Seen on real Windows: every step printed OK, then the tool exited with 1.
        var result = new RepairService(new FakeRunner(0, 0, 1), new FakeMover()).Run(new RepairRequest("network"));

        Assert.True(result.Ok);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 2)]
    public void Network_ReportsRealFailures(int flush, int winsock, int ip)
    {
        var result = new RepairService(new FakeRunner(flush, winsock, ip), new FakeMover()).Run(new RepairRequest("network"));

        Assert.False(result.Ok);
    }
}
