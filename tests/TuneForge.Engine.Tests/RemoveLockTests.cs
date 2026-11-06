using TuneForge.Contracts;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

public sealed class RemoveLockTests : IDisposable
{
    private const int Build = 26300;
    private const string User = "S-1-5-21-1-2-3-1001";
    private const string History = "privacy.clipboard-history";
    private const string PolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\System";

    private readonly string _folder = Directory.CreateTempSubdirectory("tuneforge-locks").FullName;
    private readonly TweakCatalog _catalog = TweakCatalog.LoadEmbedded();
    private readonly FakeRegistry _registry = new();
    private readonly Journal _journal;
    private readonly TweakEngine _engine;
    private readonly TweakScanner _scanner;

    public RemoveLockTests()
    {
        _journal = Journal.OpenForWriting(Path.Combine(_folder, "journal.db"));
        _engine = new TweakEngine(_catalog, _registry, _journal, new FakeRestorePoints(), User, Build, "1.0.0");
        _scanner = new TweakScanner(_catalog, _registry, Build);
    }

    public void Dispose()
    {
        _journal.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private bool Locked(string id) => _scanner.Scan(_journal.ActiveWrites()).Single(item => item.Id == id).Locked;

    private void SetFromOutside(string value = "0") =>
        _registry.Write("HKLM", PolicyPath, "AllowClipboardHistory", new RegistryValue("dword", value));

    private RegistryValue? Policy() => _registry.Read("HKLM", PolicyPath, "AllowClipboardHistory");

    [Fact]
    public void APolicySetByAnotherToolIsLocked()
    {
        SetFromOutside();

        Assert.True(Locked(History));
    }

    [Fact]
    public void NothingSetMeansNotLocked()
    {
        Assert.False(Locked(History));
    }

    [Fact]
    public void ADifferentValueIsNotThisTweaksLock()
    {
        SetFromOutside("1");

        Assert.False(Locked(History));
        Assert.Throws<CatalogRequestException>(() => _engine.RemoveLock(History));
        Assert.Equal("1", Policy()!.Data);
    }

    [Fact]
    public void WhatTuneForgeSetItselfIsLeftToHistory()
    {
        _engine.Apply(new ApplyRequest([History], false));

        Assert.False(Locked(History));
    }

    [Fact]
    public void RemovesOnlyThePolicyAndLeavesOrdinarySettingsAlone()
    {
        SetFromOutside();
        _registry.Write("HKCU", @"Software\Microsoft\Clipboard", "EnableClipboardHistory", new RegistryValue("dword", "0"));

        var result = _engine.RemoveLock(History);

        Assert.True(result.Ok);
        Assert.Null(Policy());
        Assert.NotNull(_registry.Read("HKCU", @"Software\Microsoft\Clipboard", "EnableClipboardHistory"));
        Assert.False(Locked(History));
    }

    [Fact]
    public void HistoryCanPutTheLockBackExactly()
    {
        SetFromOutside();
        _engine.RemoveLock(History);
        var change = _journal.ListChangeSets().First();

        var undo = _engine.Undo(change.Id);

        Assert.Empty(undo.Problems);
        Assert.Equal("0", Policy()!.Data);
        Assert.True(Locked(History));
    }

    [Fact]
    public void AnUnknownTweakIsRefused()
    {
        Assert.Throws<CatalogRequestException>(() => _engine.RemoveLock("system.format-disk"));
    }

    [Fact]
    public void ATweakWithNoPolicyHasNoLock()
    {
        var plain = _catalog.Items.First(item => item.Supports(Build) && !item.Actions.Any(action => action.IsPolicy));

        Assert.Throws<CatalogRequestException>(() => _engine.RemoveLock(plain.Id));
    }

    [Fact]
    public void AnUndoneChangeNoLongerCountsAsTuneForges()
    {
        var applied = _engine.Apply(new ApplyRequest([History], false));
        _engine.Undo(applied.ChangeSetId);
        SetFromOutside();

        Assert.True(Locked(History));
    }

    [Theory]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\System", true)]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", true)]
    [InlineData(@"Software\Microsoft\Clipboard", false)]
    public void KnowsWhatIsAPolicy(string path, bool expected)
    {
        Assert.Equal(expected, new RegistryAction("HKLM", path, "Name").IsPolicy);
    }

    [Fact]
    public void EveryLockInTheCatalogRemovesAndRestoresCleanly()
    {
        foreach (var item in _catalog.Items.Where(item => item.Supports(Build) && item.Actions.Any(action => action.IsPolicy && !action.Delete)))
        {
            foreach (var action in item.Actions.Where(action => action.IsPolicy && !action.Delete))
            {
                _registry.Write(action.Hive, action.Path, action.Name, action.Target!);
            }

            Assert.True(_engine.RemoveLock(item.Id).Ok, item.Id);
            Assert.All(item.Actions.Where(action => action.IsPolicy && !action.Delete), action => Assert.Null(_registry.Read(action.Hive, action.Path, action.Name)));
        }
    }
}
