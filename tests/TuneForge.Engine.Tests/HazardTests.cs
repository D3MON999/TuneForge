using TuneForge.Contracts;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

public class HazardTests
{
    private static readonly TweakCatalog Catalog = TweakCatalog.LoadEmbedded();

    private sealed class NoRunner : IProcessRunner
    {
        public int Calls { get; private set; }

        public int Run(string tool, IReadOnlyList<string> arguments, TimeSpan timeLimit)
        {
            Calls++;
            return 0;
        }
    }

    private sealed class State : IAdvancedState
    {
        public IReadOnlySet<string> Enabled() => new HashSet<string>();

        public void Save(IReadOnlySet<string> ids)
        {
        }
    }

    private sealed class Adapters : IAdapterSettings
    {
        public void SetNetbios(bool off)
        {
        }

        public void SetNagle(bool off)
        {
        }
    }

    [Fact]
    public void TheExpectedTweaksAreMarked()
    {
        Assert.Equal(["advanced.timer-resolution", "advanced.wpbt"], Catalog.Items.Where(item => item.Hazard).Select(item => item.Id).Order());
    }

    [Fact]
    public void HazardousTweaksAreHighRiskAndInNoPreset()
    {
        foreach (var item in Catalog.Items.Where(item => item.Hazard))
        {
            Assert.Equal("high", item.Risk);
            Assert.Empty(item.Presets);
        }
    }

    [Theory]
    [InlineData("hpet")]
    [InlineData("dynamic-tick")]
    [InlineData("legacy-boot-menu")]
    public void BootSwitchesAreMarkedAndRefusedWithoutAYes(string id)
    {
        var runner = new NoRunner();
        var service = new AdvancedService(runner, new State(), new Adapters());

        Assert.True(service.List().Single(item => item.Id == id).Hazard);
        Assert.Throws<CatalogRequestException>(() => service.Set(new AdvancedSetRequest(id, true)));
        Assert.Equal(0, runner.Calls);

        Assert.True(service.Set(new AdvancedSetRequest(id, true, AcceptHazard: true)).Ok);
        Assert.True(service.Set(new AdvancedSetRequest(id, false)).Ok);
    }
}
