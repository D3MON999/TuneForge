using TuneForge.Contracts;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

public class ProtectedProcessesTests
{
    [Theory]
    [InlineData("vgc")]
    [InlineData("VALORANT-Win64-Shipping")]
    [InlineData("riotclientservices")]
    [InlineData("EasyAntiCheat_EOS")]
    [InlineData("GTA5_Enhanced_BE")]
    [InlineData("FortniteClient-Win64-Shipping_EAC_EOS")]
    public void KnowsAntiCheatAndProtectedGames(string name)
    {
        Assert.True(ProtectedProcesses.IsProtected(name));
        Assert.True(ProcessService.IsLocked(name));
    }

    [Fact]
    public void LeavesOrdinaryAppsAlone()
    {
        Assert.False(ProtectedProcesses.IsProtected("Discord"));
        Assert.False(ProtectedProcesses.AnyRunning(["chrome", "Spotify"]));
        Assert.True(ProtectedProcesses.AnyRunning(["chrome", "vgc"]));
    }

    [Theory]
    [InlineData("vgc")]
    [InlineData("VGK")]
    [InlineData("EasyAntiCheat_EOS")]
    public void KnowsAntiCheatServices(string name) => Assert.True(ProtectedProcesses.IsProtectedService(name));

    [Theory]
    [InlineData("Riot Vanguard", "x.exe")]
    [InlineData("Tray", "\"C:\\Program Files\\Riot Vanguard\\vgtray.exe\"")]
    public void KnowsVanguardStartup(string name, string command) =>
        Assert.True(ProtectedProcesses.IsProtectedStartup(name, command));

    [Fact]
    public void RefusesToEndAnAntiCheatProcess()
    {
        Assert.Throws<CatalogRequestException>(() => new ProcessService().End(new ProcessEndRequest(1234, "vgc")));
    }
}

public sealed class BoostNearAntiCheatTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tf-near-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private sealed class Settings : IGameSettings
    {
        public string? ReadGpu(string executable) => null;

        public void WriteGpu(string executable, string? value)
        {
        }

        public string? ReadFullscreen(string executable) => null;

        public void WriteFullscreen(string executable, string? value)
        {
        }

        public int? ReadToasts() => 1;

        public void WriteToasts(int? value)
        {
        }
    }

    private sealed class Plans : IPowerPlans
    {
        public IReadOnlyList<PowerPlan> List() => [new("a", "A", true)];

        public bool Activate(string id) => true;

        public bool AddUltimate() => true;
    }

    private sealed class Apps(params string[] running) : IRunningApps
    {
        public List<string> Prioritised { get; } = [];

        public IReadOnlySet<string> ProcessNames() => running.ToHashSet(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> WindowedApps() => [];

        public void SetHighPriority(IEnumerable<string> names) => Prioritised.AddRange(names);

        public void CloseApps(IEnumerable<string> names)
        {
        }
    }

    [Fact]
    public void HighPriorityIsSkippedWhileAnyAntiCheatRuns()
    {
        var apps = new Apps("Game", "vgc");
        var booster = new GameBooster(new SettingsStore(Path.Combine(_folder, "s.json")), new Settings(), new Plans(), apps, (_, _) => { });
        booster.Save(new GameEntry("g", "Game", "Steam", "C:\\", "C:\\g.exe", ["Game"], null, new GameBoosts()), new GameBoosts(HighPriority: true));

        booster.Tick();

        Assert.Empty(apps.Prioritised);
    }
}
