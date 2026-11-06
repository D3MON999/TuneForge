using System.Security.Principal;
using TuneForge.Contracts;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

/// <summary>Safe stand-in for the registry. These tests never touch the real one.</summary>
internal sealed class FakeRegistry : IRegistryStore
{
    private readonly Dictionary<string, RegistryValue> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

    public Func<string, bool> Refuse { get; set; } = _ => false;

    public IReadOnlyDictionary<string, RegistryValue> Values => _values;

    public IReadOnlySet<string> Keys => _keys;

    private static string Key(string hive, string path) => $@"{hive}\{path}";

    private static string Id(string hive, string path, string name) => $@"{hive}\{path}|{name}";

    public bool KeyExists(string hive, string path) => _keys.Contains(Key(hive, path));

    public RegistryValue? Read(string hive, string path, string name) =>
        _values.GetValueOrDefault(Id(hive, path, name));

    public void Write(string hive, string path, string name, RegistryValue value)
    {
        if (Refuse(path))
        {
            throw new UnauthorizedAccessException();
        }

        _keys.Add(Key(hive, path));
        _values[Id(hive, path, name)] = value;
    }

    public void DeleteValue(string hive, string path, string name) => _values.Remove(Id(hive, path, name));

    public void DeleteKeyIfEmpty(string hive, string path)
    {
        if (!_values.Keys.Any(id => id.StartsWith(Key(hive, path) + "|", StringComparison.OrdinalIgnoreCase)))
        {
            _keys.Remove(Key(hive, path));
        }
    }
}

internal sealed class FakeRestorePoints : IRestorePointService
{
    public int Calls { get; private set; }

    public string Create(string description)
    {
        Calls++;
        return "requested";
    }
}

public sealed class TweakEngineTests : IDisposable
{
    private const int Build = 26300;
    private const string User = "S-1-5-21-1-2-3-1001";

    private readonly string _folder = Directory.CreateTempSubdirectory("tuneforge-tests").FullName;
    private readonly TweakCatalog _catalog = TweakCatalog.LoadEmbedded();
    private readonly FakeRegistry _registry = new();
    private readonly FakeRestorePoints _restore = new();
    private readonly Journal _journal;
    private readonly TweakEngine _engine;
    private readonly TweakScanner _scanner;

    public TweakEngineTests()
    {
        _journal = Journal.OpenForWriting(Path.Combine(_folder, "journal.db"));
        _engine = Engine(User);
        _scanner = new TweakScanner(_catalog, _registry, Build);
    }

    private TweakEngine Engine(string user) =>
        new(_catalog, _registry, _journal, _restore, user, Build, "1.0.0");

    public static TheoryData<string> AllTweaks() =>
        new(TweakCatalog.LoadEmbedded().Items.Select(item => item.Id));

    private TweakStatus Status(string id) => _scanner.Scan().Single(item => item.Id == id);

    private TweakItem Supported(Func<TweakItem, bool> filter) =>
        _catalog.Items.First(item => item.Supports(Build) && filter(item));

    [Theory]
    [MemberData(nameof(AllTweaks))]
    public void EveryTweak_AppliesVerifiesUndoesAndReapplies(string id)
    {
        var item = _catalog.Items.Single(candidate => candidate.Id == id);
        if (!item.Supports(Build))
        {
            var skipped = _engine.Apply(new ApplyRequest([id], false));
            Assert.Equal(TweakStates.Unsupported, skipped.Results.Single().Status);
            return;
        }

        // Give the first setting an existing value, so undo has something real to restore.
        var seed = item.Actions[0];
        var original = new RegistryValue("dword", "12345");
        _registry.Write(seed.Hive, seed.Path, seed.Name, original);

        var first = _engine.Apply(new ApplyRequest([id], false));
        Assert.Equal(TweakStates.Applied, first.Results.Single().Status);
        Assert.Equal(TweakStates.Applied, Status(id).State);

        var undo = _engine.Undo(first.ChangeSetId);
        Assert.Empty(undo.Problems);
        Assert.Equal(original, _registry.Read(seed.Hive, seed.Path, seed.Name));

        var again = _engine.Apply(new ApplyRequest([id], false));
        Assert.Equal(TweakStates.Applied, again.Results.Single().Status);
        Assert.Equal(TweakStates.Applied, Status(id).State);
    }

    [Fact]
    public void Undo_RemovesValuesAndKeysThatDidNotExistBefore()
    {
        var item = Supported(candidate => candidate.Actions.All(action => !action.Delete));
        var result = _engine.Apply(new ApplyRequest([item.Id], false));
        Assert.NotEmpty(_registry.Values);

        _engine.Undo(result.ChangeSetId);

        Assert.Empty(_registry.Values);
        Assert.Empty(_registry.Keys);
    }

    [Fact]
    public void PartialFailure_LeavesNothingChanged()
    {
        var item = Supported(candidate =>
            candidate.Actions.Count > 1
            && !candidate.Actions.Any(action => action.Delete)
            && candidate.Actions.Select(action => action.Path).Distinct().Count() > 1);
        var lastPath = item.Actions[^1].Path;
        _registry.Refuse = path => path == lastPath;

        var result = _engine.Apply(new ApplyRequest([item.Id], false));

        Assert.Equal("failed", result.Results.Single().Status);
        Assert.Empty(_registry.Values);
        Assert.Empty(_journal.ListChangeSets().Single().TweakIds);
    }

    [Fact]
    public void Undo_LeavesSettingsThatSomethingElseChanged()
    {
        var item = Supported(candidate => candidate.Actions.Count == 1 && !candidate.Actions[0].Delete);
        var action = item.Actions[0];
        var result = _engine.Apply(new ApplyRequest([item.Id], false));

        var theirs = new RegistryValue("string", "changed by the user");
        _registry.Write(action.Hive, action.Path, action.Name, theirs);
        var undo = _engine.Undo(result.ChangeSetId);

        Assert.Single(undo.Problems);
        Assert.Equal(theirs, _registry.Read(action.Hive, action.Path, action.Name));
    }

    [Fact]
    public void Undo_IsRefusedForAnotherWindowsUser()
    {
        var item = Supported(_ => true);
        var result = _engine.Apply(new ApplyRequest([item.Id], false));

        Assert.Throws<CatalogRequestException>(() => Engine("S-1-5-21-9-9-9-1002").Undo(result.ChangeSetId));
    }

    [Fact]
    public void Apply_RefusesUnknownTweaks()
    {
        Assert.Throws<CatalogRequestException>(() => _engine.Apply(new ApplyRequest(["system.format-disk"], false)));
        Assert.Empty(_journal.ListChangeSets());
    }

    [Fact]
    public void Apply_RefusesTwoChoicesFromOneGroup()
    {
        var group = _catalog.Items.Where(item => item.Group == "explorer-opens-to").Take(2).Select(item => item.Id).ToList();

        Assert.Throws<CatalogRequestException>(() => _engine.Apply(new ApplyRequest(group, false)));
        Assert.Empty(_registry.Values);
    }

    [Fact]
    public void Apply_CreatesRestorePointOnlyWhenAsked()
    {
        var item = Supported(_ => true);

        _engine.Apply(new ApplyRequest([item.Id], false));
        Assert.Equal(0, _restore.Calls);

        var result = _engine.Apply(new ApplyRequest([item.Id], true));
        Assert.Equal(1, _restore.Calls);
        Assert.Equal("requested", result.RestorePoint);
    }

    [Fact]
    public void Preview_ListsChangesWithoutMakingThem()
    {
        var item = Supported(candidate => candidate.Actions.Any(action => !action.Delete));

        var preview = _scanner.Preview([item.Id]).Single();

        Assert.NotEmpty(preview.Changes);
        Assert.Empty(_registry.Values);
    }

    public void Dispose()
    {
        _journal.Dispose();
        Directory.Delete(_folder, recursive: true);
    }
}

public class TweakCatalogTests
{
    private static readonly TweakCatalog Catalog = TweakCatalog.LoadEmbedded();

    [Fact]
    public void Catalog_HasEnoughItems() => Assert.True(Catalog.Items.Count >= 80);

    [Fact]
    public void Ids_AreUnique() =>
        Assert.Equal(Catalog.Items.Count, Catalog.Items.Select(item => item.Id).Distinct().Count());

    [Fact]
    public void EveryItem_IsWellFormed()
    {
        var groups = Catalog.Document.Groups.Select(group => group.Id).ToHashSet();
        foreach (var item in Catalog.Items)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Title), item.Id);
            Assert.Contains(item.Risk, new[] { "low", "medium" });
            Assert.Contains(item.Source, new[] { "Win11Debloat", "WinUtil" });
            Assert.Contains(item.Restart, new[] { "none", "explorer", "signout", "reboot" });
            Assert.NotEmpty(item.Actions);
            if (item.Group is not null)
            {
                Assert.Contains(item.Group, groups);
            }

            if (item.Risk == "medium")
            {
                Assert.False(string.IsNullOrWhiteSpace(item.Impact), $"{item.Id} must say what you give up.");
            }

            foreach (var action in item.Actions)
            {
                Assert.Contains(action.Hive, new[] { "HKCU", "HKLM" });
                Assert.False(action.Path.StartsWith('\\'), item.Id);
                if (!action.Delete)
                {
                    Assert.Contains(action.Kind, new[] { "dword", "qword", "string", "binary" });
                    Assert.NotNull(action.Target);
                }
            }
        }
    }

    [Fact]
    public void Catalog_HasNoSecurityReducingSettings()
    {
        var banned = new[] { "DisableAntiSpyware", "DisableRealtimeMonitoring", "SmartScreen", "EnableVirtualizationBasedSecurity", "HypervisorEnforcedCodeIntegrity", "EnableLUA" };
        var names = Catalog.Items.SelectMany(item => item.Actions).Select(action => $@"{action.Path}\{action.Name}");

        Assert.DoesNotContain(names, name => banned.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("minimal", "standard")]
    [InlineData("standard", "advanced")]
    public void LargerPresets_IncludeSmallerOnes(string smaller, string larger) =>
        Assert.Subset(Catalog.PresetIds(larger).ToHashSet(), Catalog.PresetIds(smaller).ToHashSet());
}

/// <summary>Uses a private key under HKCU\Software, created and removed by the test.</summary>
public sealed class WindowsRegistryStoreTests : IDisposable
{
    private const string Path = @"Software\TuneForgeTests\Case";
    private readonly WindowsRegistryStore _store;

    public WindowsRegistryStoreTests()
    {
        using var identity = WindowsIdentity.GetCurrent();
        _store = new WindowsRegistryStore(identity.User!);
    }

    [Theory]
    [InlineData("dword", "4294967295")]
    [InlineData("qword", "18446744073709551615")]
    [InlineData("string", "text")]
    [InlineData("expand", "%TEMP%\\x")]
    [InlineData("multi", "one\0two")]
    [InlineData("binary", "90120780")]
    public void Values_RoundTripExactly(string kind, string data)
    {
        var value = new RegistryValue(kind, data);

        _store.Write("HKCU", Path, "Value", value);

        Assert.Equal(value, _store.Read("HKCU", Path, "Value"));
    }

    [Fact]
    public void MissingValue_ReadsAsNull() => Assert.Null(_store.Read("HKCU", Path, "Missing"));

    [Fact]
    public void EmptyKey_IsRemoved_ButKeyWithDataIsKept()
    {
        _store.Write("HKCU", Path, "Value", new RegistryValue("dword", "1"));

        _store.DeleteKeyIfEmpty("HKCU", Path);
        Assert.True(_store.KeyExists("HKCU", Path));

        _store.DeleteValue("HKCU", Path, "Value");
        _store.DeleteKeyIfEmpty("HKCU", Path);
        Assert.False(_store.KeyExists("HKCU", Path));
    }

    public void Dispose() => Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\TuneForgeTests", throwOnMissingSubKey: false);
}
