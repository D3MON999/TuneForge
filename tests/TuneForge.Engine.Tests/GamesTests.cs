using TuneForge.Contracts;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

public sealed class GameScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tf-games-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private string Sized(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private void Text(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void SteamApp(string library, string id, string name, string folder) => Text(
        Path.Combine(library, "steamapps", $"appmanifest_{id}.acf"),
        $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{id}\"\n\t\"name\"\t\t\"{name}\"\n\t\"installdir\"\t\t\"{folder}\"\n}}\n");

    private GameScanner Scanner() => new(new GameSources(Path.Combine(_root, "Steam"), Path.Combine(_root, "Epic"), []));

    [Fact]
    public void Steam_FindsGamesInEveryLibraryAndSkipsTheRedistributables()
    {
        var second = Path.Combine(_root, "Library2");
        Text(@"Steam\steamapps\libraryfolders.vdf", $"\"libraryfolders\"\n{{\n\"1\"\n{{\n\"path\"\t\t\"{second.Replace(@"\", @"\\")}\"\n}}\n}}");
        SteamApp("Steam", "228980", "Steamworks Common Redistributables", "Steamworks Shared");
        Sized(@"Steam\steamapps\common\Steamworks Shared\thing.exe", 10);
        SteamApp("Library2", "100", "Big Game", "BigGame");
        Sized(@"Library2\steamapps\common\BigGame\Game.exe", 500);
        Sized(@"Library2\steamapps\common\BigGame\unins000.exe", 900);
        Sized(@"Library2\steamapps\common\BigGame\tools\CrashReporter.exe", 900);
        Sized(@"Library2\steamapps\common\BigGame\tools\Editor.exe", 100);

        var game = Assert.Single(Scanner().Scan([]));

        Assert.Equal("steam-100", game.Id);
        Assert.Equal("Big Game", game.Title);
        Assert.EndsWith("Game.exe", game.Executable);
        Assert.Equal(["Game", "Editor"], game.ProcessNames);
        Assert.Null(game.AntiCheat);
    }

    [Fact]
    public void Steam_SkipsGamesWithNoFilesOnDisk()
    {
        SteamApp("Steam", "100", "Removed", "Gone");

        Assert.Empty(Scanner().Scan([]));
    }

    [Theory]
    [InlineData(@"EasyAntiCheat\EasyAntiCheat_Setup.dat", "Easy Anti-Cheat")]
    [InlineData(@"BattlEye\BEClient.dll", "BattlEye")]
    public void FindsAntiCheat(string marker, string name)
    {
        SteamApp("Steam", "100", "Online", "Online");
        Sized(@"Steam\steamapps\common\Online\Online.exe", 100);
        Sized(Path.Combine(@"Steam\steamapps\common\Online", marker), 1);

        Assert.Equal(name, Assert.Single(Scanner().Scan([])).AntiCheat);
    }

    [Fact]
    public void Epic_ReadsManifestsAndIgnoresDamagedOnes()
    {
        var folder = Path.Combine(_root, "EpicGame").Replace(@"\", @"\\");
        Text(@"Epic\good.item", $"{{\"DisplayName\":\"Epic Game\",\"InstallLocation\":\"{folder}\"}}");
        Text(@"Epic\bad.item", "not json");
        Sized(@"EpicGame\Win64\Shipping.exe", 100);

        var game = Assert.Single(Scanner().Scan([]));

        Assert.Equal("epic-good", game.Id);
        Assert.Equal("Epic Games", game.Launcher);
    }

    [Fact]
    public void Gog_PrefersTheExecutableItNames()
    {
        var folder = Path.Combine(_root, "Gog");
        var named = Sized(@"Gog\Small.exe", 10);
        Sized(@"Gog\Bigger.exe", 100);

        var game = Assert.Single(new GameScanner(new GameSources(null, Path.Combine(_root, "None"), [new GogGame("Gog Game", folder, named)])).Scan([]));

        Assert.Equal(named, game.Executable);
    }

    [Fact]
    public void ShowsSavedBoosts()
    {
        SteamApp("Steam", "100", "Game", "Game");
        var exe = Sized(@"Steam\steamapps\common\Game\Game.exe", 100);
        var boosts = new GameBoosts(PowerBoost: true);

        var game = Assert.Single(Scanner().Scan([new GameProfile("steam-100", "Game", exe, ["Game"], null, boosts)]));

        Assert.Equal(boosts, game.Boosts);
    }
}

public class GameFlagTextTests
{
    [Theory]
    [InlineData(null, true, "GpuPreference=2;")]
    [InlineData("GpuPreference=1;SwapEffectUpgradeEnable=1;", true, "GpuPreference=2;SwapEffectUpgradeEnable=1;")]
    [InlineData("GpuPreference=2;SwapEffectUpgradeEnable=1;", false, "SwapEffectUpgradeEnable=1;")]
    [InlineData("GpuPreference=2;", false, null)]
    public void GpuKeepsOtherSettings(string? value, bool on, string? expected)
    {
        Assert.Equal(expected, GameFlagText.WithGpu(value, on));
    }

    [Theory]
    [InlineData(null, true, "~ DISABLEDXMAXIMIZEDWINDOWEDMODE")]
    [InlineData("~ RUNASADMIN", true, "~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE")]
    [InlineData("~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE", false, "~ RUNASADMIN")]
    [InlineData("~ DISABLEDXMAXIMIZEDWINDOWEDMODE", false, null)]
    [InlineData(null, false, null)]
    public void FullscreenKeepsOtherSettings(string? value, bool on, string? expected)
    {
        Assert.Equal(expected, GameFlagText.WithFullscreen(value, on));
    }
}

public class WindowsPowerPlansTests
{
    private const string Listing = """

        Existing Power Schemes (* Active)
        -----------------------------------
        Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)
        Power Scheme GUID: 93fb17df-b235-49f3-b6e0-57dc9cc8fb09  (Ultimate Performance (Custom)) *
        """;

    [Fact]
    public void ParsesNamesAndTheActivePlan()
    {
        var plans = WindowsPowerPlans.Parse(Listing);

        Assert.Equal(2, plans.Count);
        Assert.False(plans[0].Active);
        Assert.Equal("Ultimate Performance (Custom)", plans[1].Name);
        Assert.True(plans[1].Active);
    }

    [Fact]
    public void ActivateOnlyAcceptsAPlanId()
    {
        var tool = new FakeTool();

        Assert.Throws<CatalogRequestException>(() => new WindowsPowerPlans(tool).Activate("/hibernate off"));
        Assert.Empty(tool.Calls);
    }
}

public class ResizeBarCheckTests
{
    [Theory]
    [InlineData("BAR1 Memory Usage\n  Total : 256 MiB\n  Used : 2 MiB", "off")]
    [InlineData("BAR1 Memory Usage\n  Total : 12288 MiB\n  Used : 2 MiB", "on")]
    [InlineData("BAR1 Memory Usage\n  Total : N/A", "unknown")]
    [InlineData("nothing useful", "unknown")]
    public void ReadsTheBarSize(string text, string expected)
    {
        Assert.Equal(expected, ResizeBarCheck.Parse(text));
    }
}

public sealed class GameBoosterTests : IDisposable
{
    private const string Normal = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tf-boost-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsStore _store;
    private readonly FakeSettings _settings = new();
    private readonly FakePlans _plans = new();
    private readonly FakeApps _apps = new();
    private readonly List<string> _notes = [];

    public GameBoosterTests() => _store = new SettingsStore(Path.Combine(_folder, "settings.json"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private sealed class FakeSettings : IGameSettings
    {
        public Dictionary<string, string?> Gpu { get; } = [];

        public Dictionary<string, string?> Fullscreen { get; } = [];

        public int? Toasts { get; set; } = 1;

        public string? ReadGpu(string executable) => Gpu.GetValueOrDefault(executable);

        public void WriteGpu(string executable, string? value) => Gpu[executable] = value;

        public string? ReadFullscreen(string executable) => Fullscreen.GetValueOrDefault(executable);

        public void WriteFullscreen(string executable, string? value) => Fullscreen[executable] = value;

        public int? ReadToasts() => Toasts;

        public void WriteToasts(int? value) => Toasts = value;
    }

    private sealed class FakePlans : IPowerPlans
    {
        public string Active { get; set; } = Normal;

        public IReadOnlyList<PowerPlan> List() => [new(Active, "Plan", true)];

        public bool Activate(string id)
        {
            Active = id;
            return true;
        }

        public bool AddUltimate() => true;
    }

    private sealed class FakeApps : IRunningApps
    {
        public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Prioritised { get; } = [];

        public List<string> Closed { get; } = [];

        public IReadOnlySet<string> ProcessNames() => Running;

        public IReadOnlyList<string> WindowedApps() => [];

        public void SetHighPriority(IEnumerable<string> names) => Prioritised.AddRange(names);

        public void CloseApps(IEnumerable<string> names) => Closed.AddRange(names);
    }

    private GameBooster Booster() => new(_store, _settings, _plans, _apps, (_, message) => _notes.Add(message));

    private static GameEntry Game(string? antiCheat = null) =>
        new("steam-1", "Game", "Steam", @"C:\Game", @"C:\Game\Game.exe", ["Game", "GameHelper"], antiCheat, new GameBoosts());

    [Fact]
    public void Save_WritesWindowsSettingsAndClearingThemRemovesTheProfile()
    {
        var booster = Booster();
        booster.Save(Game(), new GameBoosts(HighPerformanceGpu: true, DisableFullscreenOptimizations: true));

        Assert.Equal(GameFlagText.GpuHighPerformance, _settings.Gpu[@"C:\Game\Game.exe"]);
        Assert.Contains(GameFlagText.FullscreenOff, _settings.Fullscreen[@"C:\Game\Game.exe"]);
        Assert.Single(booster.Profiles);
        Assert.False(booster.HasWork);

        booster.Save(Game(), new GameBoosts());

        Assert.Null(_settings.Gpu[@"C:\Game\Game.exe"]);
        Assert.Empty(booster.Profiles);
    }

    [Fact]
    public void BoostsWhileRunningThenPutsEverythingBack()
    {
        _store.Update(settings => settings with { FocusApps = ["Discord"] });
        var booster = Booster();
        booster.Save(Game(), new GameBoosts(HighPriority: true, PowerBoost: true, Focus: true));
        Assert.True(booster.HasWork);

        booster.Tick();
        Assert.Equal(Normal, _plans.Active);

        _apps.Running.Add("gamehelper");
        booster.Tick();
        booster.Tick();

        Assert.Equal(WindowsPowerPlans.HighPerformance, _plans.Active);
        Assert.Equal(0, _settings.Toasts);
        Assert.Equal(["Discord"], _apps.Closed);
        Assert.Equal(["Game", "GameHelper"], _apps.Prioritised);

        _apps.Running.Clear();
        booster.Tick();

        Assert.Equal(Normal, _plans.Active);
        Assert.Equal(1, _settings.Toasts);
        Assert.Null(_store.Load().Boost);
    }

    [Fact]
    public void AntiCheatGamesGetNoPriorityChange()
    {
        var booster = Booster();
        booster.Save(Game("BattlEye"), new GameBoosts(HighPriority: true, PowerBoost: true));
        _apps.Running.Add("Game");

        booster.Tick();

        Assert.Empty(_apps.Prioritised);
        Assert.Equal(WindowsPowerPlans.HighPerformance, _plans.Active);
    }

    [Fact]
    public void LeavesSettingsAloneThatWereAlreadyThere()
    {
        _plans.Active = WindowsPowerPlans.HighPerformance;
        _settings.Toasts = 0;
        var booster = Booster();
        booster.Save(Game(), new GameBoosts(PowerBoost: true, Focus: true));
        _apps.Running.Add("Game");
        booster.Tick();

        _apps.Running.Clear();
        _plans.Active = Normal;
        _settings.Toasts = 1;
        booster.Tick();

        Assert.Equal(Normal, _plans.Active);
        Assert.Equal(1, _settings.Toasts);
    }

    [Fact]
    public void RestoresAMissingToastSetting()
    {
        _settings.Toasts = null;
        var booster = Booster();
        booster.Save(Game(), new GameBoosts(Focus: true));
        _apps.Running.Add("Game");
        booster.Tick();
        Assert.Equal(0, _settings.Toasts);

        _apps.Running.Clear();
        booster.Tick();

        Assert.Null(_settings.Toasts);
    }

    [Fact]
    public void Recover_PutsThingsBackAfterACrash()
    {
        Booster().Save(Game(), new GameBoosts(PowerBoost: true));
        _apps.Running.Add("Game");
        Booster().Tick();

        _apps.Running.Clear();
        Booster().Recover();

        Assert.Equal(Normal, _plans.Active);
        Assert.Null(_store.Load().Boost);
    }

    [Fact]
    public void Recover_LeavesARunningGameBoosted()
    {
        Booster().Save(Game(), new GameBoosts(PowerBoost: true));
        _apps.Running.Add("Game");
        Booster().Tick();

        Booster().Recover();

        Assert.Equal(WindowsPowerPlans.HighPerformance, _plans.Active);
    }

    [Fact]
    public void RemovingAProfileDuringAGameStillRestores()
    {
        var booster = Booster();
        booster.Save(Game(), new GameBoosts(PowerBoost: true));
        _apps.Running.Add("Game");
        booster.Tick();

        booster.Save(Game(), new GameBoosts());
        booster.Tick();

        Assert.Equal(Normal, _plans.Active);
    }

    [Fact]
    public void SavingSettingsKeepsProfilesAndABoostInProgress()
    {
        var booster = Booster();
        booster.Save(Game(), new GameBoosts(PowerBoost: true));
        _apps.Running.Add("Game");
        booster.Tick();

        _store.SaveChoices(new SettingsUpdate(CloseToTray: true));

        Assert.Single(_store.Load().Profiles!);
        Assert.NotNull(_store.Load().Boost);
    }
}
