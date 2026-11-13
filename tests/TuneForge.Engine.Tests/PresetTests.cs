using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

/// <summary>Presets are clicked without reading, so they hold only changes that are safe on any PC.</summary>
public class PresetTests
{
    private static readonly TweakCatalog Catalog = TweakCatalog.LoadEmbedded();

    [Theory]
    [InlineData("advanced.bitlocker-auto")]
    [InlineData("windows.print-spooler")]
    [InlineData("privacy.disable-find-my-device")]
    [InlineData("performance.app-timeouts")]
    [InlineData("performance.service-timeout")]
    [InlineData("privacy.error-reporting")]
    [InlineData("privacy.online-speech")]
    [InlineData("privacy.setting-sync")]
    [InlineData("privacy.safe-search")]
    public void ChangesThatCanBreakSomethingAreInNoPreset(string id)
    {
        Assert.Empty(Catalog.Items.Single(item => item.Id == id).Presets);
    }

    [Fact]
    public void NoPresetHoldsAHighRiskOrHazardousTweak()
    {
        Assert.DoesNotContain(Catalog.Items, item => item.Presets.Count > 0 && (item.Risk == "high" || item.Hazard));
    }

    [Theory]
    [InlineData("privacy.typing-data")]
    [InlineData("privacy.office-telemetry")]
    [InlineData("ai.disable-chrome-ai")]
    [InlineData("performance.menu-delay")]
    [InlineData("windows.long-paths")]
    public void AdvancedIncludesTheSafePrivacyAndSpeedTweaks(string id)
    {
        Assert.Contains(id, Catalog.PresetIds("advanced"));
    }

    [Fact]
    public void AdvancedCanBeAppliedInOneGo()
    {
        Assert.Equal(Catalog.PresetIds("advanced").Count, Catalog.Resolve(Catalog.PresetIds("advanced")).Count);
    }
}
