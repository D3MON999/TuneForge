using TuneForge.Contracts;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

public class PowerToolsTests
{
    private const string Plan = "Power Scheme GUID: 11111111-2222-3333-4444-555555555555  (Balanced)";

    [Fact]
    public void CreatePlan_CopiesThenNames()
    {
        var tool = new FakeTool(new ToolOutput(0, Plan), new ToolOutput(0, string.Empty));

        Assert.True(new PowerTools(tool).CreatePlan(" My Plan ").Ok);

        Assert.Equal(["/duplicatescheme", "SCHEME_CURRENT"], tool.Calls[0].Arguments);
        Assert.Equal(["/changename", "11111111-2222-3333-4444-555555555555", "My Plan"], tool.Calls[1].Arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\"name")]
    [InlineData("x/delete")]
    public void CreatePlan_RefusesBadNames(string name)
    {
        var tool = new FakeTool();

        Assert.Throws<CatalogRequestException>(() => new PowerTools(tool).CreatePlan(name));
        Assert.Empty(tool.Calls);
    }

    [Fact]
    public void ReadsThePluggedInCoreValue()
    {
        const string text = """
            Possible Settings units: %
            Current AC Power Setting Index: 0x00000064
            Current DC Power Setting Index: 0x0000000a
            """;

        Assert.Equal(100, PowerTools.ParseMinimumCores(text.ReplaceLineEndings("\r\n")));
        Assert.Null(PowerTools.ParseMinimumCores("not available"));
    }
}

public class ConnectionScannerTests
{
    private const string Sample = """

        Active Connections

          Proto  Local Address          Foreign Address        State           PID
          TCP    0.0.0.0:135            0.0.0.0:0              LISTENING       1040
          TCP    192.168.1.5:50000      20.1.2.3:443           ESTABLISHED     4242
          TCP    [::1]:6000             [::]:0                 LISTENING       99
          UDP    0.0.0.0:53             *:*                                    77
        """;

    [Fact]
    public void ReadsConnectionsAndOpenPorts()
    {
        var rows = ConnectionScanner.Parse(Sample.ReplaceLineEndings("\r\n"));

        Assert.Equal(4, rows.Count);
        Assert.True(rows[0].Listening);
        Assert.False(rows[1].Listening);
        Assert.Equal("20.1.2.3:443", rows[1].Remote);
        Assert.Equal(4242, rows[1].ProcessId);
        Assert.Equal(77, rows[3].ProcessId);
    }

    [Fact]
    public void RemembersRemoteConnections()
    {
        var scanner = new ConnectionScanner(new FakeTool(new ToolOutput(0, Sample), new ToolOutput(0, Sample)));

        scanner.Scan(_ => "App");
        var rows = scanner.Scan(_ => "App");

        Assert.Equal("App", rows[1].Program);
        Assert.Equal("App 20.1.2.3:443", Assert.Single(scanner.History()).Key);
    }
}

public class SettingsFileTests
{
    private static readonly string[] Tweaks = ["a", "b"];
    private static readonly string[] Apps = ["7zip.7zip"];

    [Fact]
    public void SavesAndLoads()
    {
        var text = SettingsFile.Build(["a"], new AppSettings(new SettingsUpdate(CloseToTray: true), FocusApps: ["Discord"]), Apps);

        var (file, result) = SettingsFile.Read(text, Tweaks, Apps);

        Assert.Equal(["a"], result.Tweaks);
        Assert.Equal(["7zip.7zip"], result.Apps);
        Assert.True(file.Choices!.CloseToTray);
        Assert.Equal(["Discord"], file.FocusApps);
    }

    [Fact]
    public void DropsAnythingTheAppDoesntKnow()
    {
        var text = SettingsFile.Build(["a", "system.format-disk"], new AppSettings(new SettingsUpdate()), ["evil.app"]);

        var result = SettingsFile.Read(text, Tweaks, Apps).Result;

        Assert.Equal(["a"], result.Tweaks);
        Assert.Empty(result.Apps);
        Assert.Equal(2, result.Ignored);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"version\":99,\"tweaks\":[]}")]
    public void RefusesOtherFiles(string text)
    {
        Assert.Throws<CatalogRequestException>(() => SettingsFile.Read(text, Tweaks, Apps));
    }

    [Fact]
    public void RefusesHugeFiles()
    {
        Assert.Throws<CatalogRequestException>(() => SettingsFile.Read(new string(' ', 2_000_000), Tweaks, Apps));
    }
}
