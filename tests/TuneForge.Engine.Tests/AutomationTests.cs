using TuneForge.Contracts;
using TuneForge.Engine.Core;
using Xunit;

namespace TuneForge.Engine.Tests;

public sealed class AutomationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tf-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsStore _store;
    private readonly List<(string Title, string Message)> _notes = [];
    private DateTimeOffset _now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    public AutomationTests() => _store = new SettingsStore(Path.Combine(_folder, "settings.json"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private sealed class FakeActions : IAutomationActions
    {
        public double Memory { get; set; } = 50;

        public int Trims { get; private set; }

        public int Cleans { get; private set; }

        public long CleanBytes { get; set; } = 5 * 1024 * 1024;

        public List<string> Startup { get; set; } = ["Discord"];

        public double MemoryPercent() => Memory;

        public MemoryResult TrimMemory()
        {
            Trims++;
            return new MemoryResult(300 * 1024 * 1024, 100 * 1024 * 1024, 1000 * 1024 * 1024, 40);
        }

        public CleanupResult CleanFiles()
        {
            Cleans++;
            return new CleanupResult(CleanBytes, 3, 0);
        }

        public IReadOnlyList<string> StartupNames() => Startup;

        public int Backups { get; private set; }

        public OperationResult BackupRegistry()
        {
            Backups++;
            return new OperationResult(true, "Saved.");
        }
    }

    private AutomationRunner Runner(FakeActions actions) =>
        new(_store, actions, () => _now, (title, message) => _notes.Add((title, message)));

    private void Choose(SettingsUpdate choices) => _store.SaveChoices(choices);

    [Fact]
    public void Settings_StartWithEverythingAutomaticOff()
    {
        var choices = _store.Load().Choices;

        Assert.False(choices.CloseToTray || choices.StartWithWindows || choices.MemoryAuto || choices.CleanAuto || choices.StartupWatch);
    }

    [Fact]
    public void Settings_SurviveAReloadAndLimitValues()
    {
        Choose(new SettingsUpdate(MemoryAuto: true, MemoryThreshold: 5, CleanDays: 999));

        var choices = new SettingsStore(Path.Combine(_folder, "settings.json")).Load().Choices;

        Assert.True(choices.MemoryAuto);
        Assert.Equal(SettingsStore.MinThreshold, choices.MemoryThreshold);
        Assert.Equal(SettingsStore.MaxDays, choices.CleanDays);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("null")]
    public void Settings_DamagedFileGivesDefaults(string text)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "settings.json"), text);

        Assert.False(_store.Load().Choices.CleanAuto);
    }

    [Fact]
    public void NothingRunsWhenSwitchedOff()
    {
        var actions = new FakeActions { Memory = 99 };

        Runner(actions).Tick();

        Assert.Equal(0, actions.Trims);
        Assert.Equal(0, actions.Cleans);
        Assert.Empty(_notes);
        Assert.Null(_store.Load().KnownStartup);
    }

    [Fact]
    public void Memory_RunsAboveTheThresholdThenRests()
    {
        Choose(new SettingsUpdate(MemoryAuto: true, MemoryThreshold: 80));
        var actions = new FakeActions { Memory = 79 };
        var runner = Runner(actions);

        runner.Tick();
        Assert.Equal(0, actions.Trims);

        actions.Memory = 90;
        runner.Tick();
        runner.Tick();
        Assert.Equal(1, actions.Trims);
        Assert.Equal("200 MB freed.", _notes.Single().Message);

        _now = _now.AddMinutes(31);
        runner.Tick();
        Assert.Equal(2, actions.Trims);
    }

    [Fact]
    public void Clean_WaitsAFullIntervalBeforeTheFirstRun()
    {
        Choose(new SettingsUpdate(CleanAuto: true, CleanDays: 7));
        var actions = new FakeActions();
        var runner = Runner(actions);

        runner.Tick();
        _now = _now.AddDays(6);
        runner.Tick();
        Assert.Equal(0, actions.Cleans);

        _now = _now.AddDays(1);
        runner.Tick();
        runner.Tick();
        Assert.Equal(1, actions.Cleans);
        Assert.Equal("Junk files cleaned", _notes.Single().Title);
    }

    [Fact]
    public void Clean_StaysQuietWhenThereWasNothingToDelete()
    {
        Choose(new SettingsUpdate(CleanAuto: true, CleanDays: 1));
        var actions = new FakeActions { CleanBytes = 0 };
        var runner = Runner(actions);
        runner.Tick();

        _now = _now.AddDays(2);
        runner.Tick();

        Assert.Equal(1, actions.Cleans);
        Assert.Empty(_notes);
    }

    [Fact]
    public void Startup_LearnsTheListThenAlertsOnlyForNewPrograms()
    {
        Choose(new SettingsUpdate(StartupWatch: true));
        var actions = new FakeActions();
        var runner = Runner(actions);

        runner.Tick();
        Assert.Empty(_notes);

        actions.Startup = ["Discord", "Sneaky"];
        runner.Tick();
        runner.Tick();
        Assert.Equal("Sneaky now starts with Windows.", _notes.Single().Message);

        actions.Startup = ["Sneaky"];
        runner.Tick();
        actions.Startup = ["Sneaky", "One", "Two"];
        runner.Tick();
        Assert.Equal("2 programs now start with Windows: One, Two.", _notes[1].Message);
    }

    [Fact]
    public void Startup_ForgetsTheListWhenSwitchedOff()
    {
        Choose(new SettingsUpdate(StartupWatch: true));
        var actions = new FakeActions();
        Runner(actions).Tick();
        Assert.NotNull(_store.Load().KnownStartup);

        Choose(new SettingsUpdate(StartupWatch: false));

        Assert.Null(_store.Load().KnownStartup);
    }

    [Fact]
    public void RegistryBackup_RunsWeekly()
    {
        Choose(new SettingsUpdate(RegistryBackup: true));
        var actions = new FakeActions();
        var runner = Runner(actions);

        runner.Tick();
        runner.Tick();
        Assert.Equal(1, actions.Backups);

        _now = _now.AddDays(7);
        runner.Tick();
        Assert.Equal(2, actions.Backups);
    }

    [Fact]
    public void Notifications_CanBeSilencedWithoutStoppingTheWork()
    {
        Choose(new SettingsUpdate(Notifications: false, MemoryAuto: true, MemoryThreshold: 60));
        var actions = new FakeActions { Memory = 90 };

        Runner(actions).Tick();

        Assert.Equal(1, actions.Trims);
        Assert.Empty(_notes);
    }

    [Fact]
    public void SavingChoicesKeepsWhatTheAppRemembers()
    {
        Choose(new SettingsUpdate(CleanAuto: true));
        Runner(new FakeActions()).Tick();
        var stamp = _store.Load().LastCleanUtc;

        Choose(new SettingsUpdate(CleanAuto: true, CleanDays: 3));

        Assert.Equal(stamp, _store.Load().LastCleanUtc);
    }

    [Fact]
    public void Autostart_QuotesThePathAndStartsInTheTray()
    {
        Assert.Equal("\"C:\\Program Files\\TF\\TuneForge.exe\" --tray", WindowsAutostart.Command(@"C:\Program Files\TF\TuneForge.exe"));
    }

    [Fact]
    public void MemoryResult_NeverReportsNegativeSavings()
    {
        Assert.Equal(0, new MemoryResult(100, 200, 1000, 1).FreedBytes);
    }
}
