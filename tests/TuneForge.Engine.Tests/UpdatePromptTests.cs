using TuneForge.Engine.Core;
using Xunit;

namespace TuneForge.Engine.Tests;

public class UpdatePromptTests
{
    private const string Assets = """
        "assets": [
          { "browser_download_url": "https://github.com/D3MON999/TuneForge/releases/download/v1.2.3/TuneForge-Setup-1.2.3.exe" },
          { "browser_download_url": "https://github.com/D3MON999/TuneForge/releases/download/v1.2.3/TuneForge-Setup-1.2.3.exe.sig" }
        ]
        """;

    private static ReleaseInfo Parse(string body) =>
        GitHubReleaseFeed.Parse($$"""{ "tag_name": "v1.2.3", "body": {{body}}, {{Assets}} }""")!;

    [Fact]
    public void AnOrdinaryReleaseIsNotImportant()
    {
        var release = Parse("\"Small fixes.\"");

        Assert.False(release.Important);
        Assert.Equal("Small fixes.", release.Notes);
    }

    [Theory]
    [InlineData("[important] Fixes a crash.")]
    [InlineData("[IMPORTANT]\\nFixes a crash.")]
    [InlineData("Fixes a crash. [important]")]
    public void TheMarkMakesItImportantAndIsHiddenFromTheNotes(string body)
    {
        var release = Parse($"\"{body}\"");

        Assert.True(release.Important);
        Assert.Equal("Fixes a crash.", release.Notes);
    }

    [Fact]
    public void AReleaseWithNoDescriptionStillWorks()
    {
        var release = Parse("null");

        Assert.False(release.Important);
        Assert.Equal(string.Empty, release.Notes);
    }

    [Fact]
    public void VeryLongNotesAreCut()
    {
        Assert.Equal(600, Parse($"\"{new string('a', 5000)}\"").Notes.Length);
    }

    [Theory]
    [InlineData("VALORANT-Win64-Shipping")]
    [InlineData("cs2")]
    public void AGameCountsAsPlaying(string name)
    {
        Assert.True(ProtectedProcesses.AnyGameRunning(["explorer", name]));
    }

    [Theory]
    [InlineData("vgc")]
    [InlineData("vgtray")]
    [InlineData("EasyAntiCheat_EOS")]
    [InlineData("BEService")]
    [InlineData("explorer")]
    public void AntiCheatLeftRunningDoesNotCountAsPlaying(string name)
    {
        Assert.False(ProtectedProcesses.AnyGameRunning([name]));
    }
}
