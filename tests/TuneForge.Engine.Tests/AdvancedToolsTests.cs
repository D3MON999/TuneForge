using TuneForge.Contracts;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

public class AdvancedServiceTests
{
    private sealed class Calls : IProcessRunner
    {
        public List<string> Lines { get; } = [];

        public int Code { get; set; }

        public int Run(string tool, IReadOnlyList<string> arguments, TimeSpan timeLimit)
        {
            Lines.Add($"{tool} {string.Join(' ', arguments)}");
            return Code;
        }
    }

    private sealed class MemoryState : IAdvancedState
    {
        public HashSet<string> Ids { get; } = [];

        public IReadOnlySet<string> Enabled() => Ids;

        public void Save(IReadOnlySet<string> ids)
        {
            var copy = ids.ToList();
            Ids.Clear();
            Ids.UnionWith(copy);
        }
    }

    private sealed class Adapters : IAdapterSettings
    {
        public List<string> Calls { get; } = [];

        public void SetNetbios(bool off) => Calls.Add($"netbios {off}");

        public void SetNagle(bool off) => Calls.Add($"nagle {off}");
    }

    private readonly Calls _calls = new();
    private readonly MemoryState _state = new();
    private readonly Adapters _adapters = new();

    private AdvancedService Service() => new(_calls, _state, _adapters);

    [Fact]
    public void EveryOption_HasADescriptionAndWarning()
    {
        foreach (var item in Service().List())
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Description), item.Id);
            Assert.False(string.IsNullOrWhiteSpace(item.Warning), item.Id);
            Assert.Contains(item.Risk, new[] { "low", "medium", "high" });
        }
    }

    [Fact]
    public void TurningOnAndOff_RunsTheFixedCommandsAndRemembers()
    {
        Assert.True(Service().Set(new AdvancedSetRequest("hibernation", true)).Ok);
        Assert.Equal("powercfg.exe /hibernate off", _calls.Lines.Single());
        Assert.True(Service().List().Single(item => item.Id == "hibernation").Enabled);

        Service().Set(new AdvancedSetRequest("hibernation", false));

        Assert.Equal("powercfg.exe /hibernate on", _calls.Lines[^1]);
        Assert.False(Service().List().Single(item => item.Id == "hibernation").Enabled);
    }

    [Fact]
    public void AFailureDoesNotCountAsOn()
    {
        _calls.Code = 5;

        Assert.False(Service().Set(new AdvancedSetRequest("dynamic-tick", true, AcceptHazard: true)).Ok);
        Assert.Empty(_state.Ids);
    }

    [Fact]
    public void DismRestartCodeCountsAsSuccess()
    {
        _calls.Code = 3010;

        var result = Service().Set(new AdvancedSetRequest("reserved-storage", true));

        Assert.True(result.Ok);
        Assert.True(result.RestartNeeded);
    }

    [Fact]
    public void AdapterOptions_GoThroughTheAdapterSettings()
    {
        Service().Set(new AdvancedSetRequest("netbios", true));
        Service().Set(new AdvancedSetRequest("nagle", false));

        Assert.Equal(["netbios True", "nagle False"], _adapters.Calls);
    }

    [Fact]
    public void MissingNvidiaTasks_AreNotAFailure()
    {
        _calls.Code = 1;

        Assert.True(Service().Set(new AdvancedSetRequest("nvidia-telemetry", true)).Ok);
    }

    [Fact]
    public void OneWayRemovals_CantBeReversed()
    {
        Assert.Throws<CatalogRequestException>(() => Service().Set(new AdvancedSetRequest("remove-onedrive", false)));
    }

    [Fact]
    public void UnknownOptions_AreRefused()
    {
        Assert.Throws<CatalogRequestException>(() => Service().Set(new AdvancedSetRequest("format-disk", true)));
        Assert.Empty(_calls.Lines);
    }
}

public class SecurityServiceTests
{
    [Fact]
    public void OnlyKnownProtectionsCanBeTurnedOn()
    {
        Assert.True(new SecurityService(new FakeRunner(0)).TurnOn(new SecuritySetRequest("firewall")).Ok);
        Assert.False(new SecurityService(new FakeRunner(1)).TurnOn(new SecuritySetRequest("controlled-folders")).Ok);
        Assert.Throws<CatalogRequestException>(() => new SecurityService(new FakeRunner(0)).TurnOn(new SecuritySetRequest("defender-off")));
    }
}

public class WindowsToolLinksTests
{
    [Fact]
    public void Links_AreUniqueAndDescribed()
    {
        Assert.Equal(WindowsToolLinks.All.Count, WindowsToolLinks.All.Select(tool => tool.Link.Id).Distinct().Count());
        Assert.All(WindowsToolLinks.All, tool => Assert.False(string.IsNullOrWhiteSpace(tool.Link.Note)));
    }

    [Fact]
    public void UnknownToolsAreRefused()
    {
        Assert.Throws<CatalogRequestException>(() => WindowsToolLinks.Open("cmd"));
    }
}

public class ProcessServiceTests
{
    [Theory]
    [InlineData("csrss")]
    [InlineData("LSASS")]
    [InlineData("explorer")]
    public void WindowsProcessesAreLocked(string name)
    {
        Assert.True(ProcessService.IsLocked(name));
        Assert.Throws<CatalogRequestException>(() => new ProcessService().End(new ProcessEndRequest(4, name)));
    }

    [Fact]
    public void ANameMismatchEndsNothing()
    {
        var result = new ProcessService().End(new ProcessEndRequest(Environment.ProcessId, "something-else"));

        Assert.False(result.Ok);
    }

    [Fact]
    public void ListsThisProcess()
    {
        Assert.Contains(new ProcessService().List(), process => process.Id == Environment.ProcessId);
    }
}

public class RepairAdditionsTests
{
    [Fact]
    public void DiskCheckScansWithoutTakingTheDriveOffline()
    {
        var runner = new FakeRunner(0);

        Assert.True(new RepairService(runner, new WindowsDirectoryMover()).Run(new RepairRequest("disk-check")).Ok);
    }

    [Fact]
    public void ComponentCleanupReportsFailure()
    {
        var runner = new FakeRunner(1);

        Assert.False(new RepairService(runner, new WindowsDirectoryMover()).Run(new RepairRequest("component-store")).Ok);
    }
}
