using TuneForge.Contracts;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

internal sealed class FakeTool(params ToolOutput[] outputs) : IToolRunner
{
    private readonly Queue<ToolOutput> _outputs = new(outputs);

    public List<(string Path, IReadOnlyList<string> Arguments)> Calls { get; } = [];

    public ToolOutput Capture(string path, IReadOnlyList<string> arguments, TimeSpan timeLimit)
    {
        Calls.Add((path, arguments));
        return _outputs.Count > 0 ? _outputs.Dequeue() : new ToolOutput(0, string.Empty);
    }
}

public class AppCatalogTests
{
    [Fact]
    public void BuiltInCatalog_NeverOffersTheStoreAndHasSafeNames()
    {
        var apps = AppCatalogs.LoadBuiltIn();

        Assert.True(apps.Count > 150);
        Assert.DoesNotContain(apps, app => app.Id == "Microsoft.WindowsStore");
        Assert.DoesNotContain(apps, app => app.Id == "Microsoft.DesktopAppInstaller");
        Assert.Equal(apps.Count, apps.Select(app => app.Id).Distinct().Count());
        Assert.All(apps, app =>
        {
            Assert.Matches("^[A-Za-z0-9.-]+$", app.Id);
            Assert.Contains(app.Safety, new[] { "recommended", "optional", "caution" });
        });
    }

    [Fact]
    public void SoftwareCatalog_HasUniquePlainIds()
    {
        var items = AppCatalogs.LoadSoftware();

        Assert.True(items.Count > 200);
        Assert.Equal(items.Count, items.Select(item => item.Id).Distinct().Count());
        Assert.All(items, item => Assert.Matches("^[A-Za-z0-9][A-Za-z0-9._+-]*$", item.Id));
    }
}

public class BuiltInAppServiceTests
{
    private static readonly BuiltInApp[] Catalog =
    [
        new("Microsoft.BingNews", "News", "n", "Microsoft", "recommended", "9WZDNCRFHVFW"),
        new("Vendor.Thing", "Thing", "n", "Other apps", "optional", null),
        new("Vendor.Gone", "Gone", "n", "Other apps", "optional", null),
    ];

    [Fact]
    public void List_ShowsInstalledAppsAndOnlyReinstallableRemovedOnes()
    {
        var tool = new FakeTool(new ToolOutput(0, "Vendor.Thing\r\nSomething.Else\r\n"));

        var list = new BuiltInAppService(tool, Catalog).List();

        Assert.Equal(["Microsoft.BingNews", "Vendor.Thing"], list.Select(app => app.Id));
        Assert.False(list[0].Installed);
        Assert.True(list[0].CanReinstall);
        Assert.True(list[1].Installed);
        Assert.False(list[1].CanReinstall);
    }

    [Fact]
    public void List_ReportsAWindowsFailure()
    {
        var tool = new FakeTool(new ToolOutput(1, string.Empty));

        Assert.Throws<CatalogRequestException>(() => new BuiltInAppService(tool, Catalog).List());
    }

    [Fact]
    public void Remove_ReportsEachAppFromTheVerifiedOutput()
    {
        var tool = new FakeTool(new ToolOutput(0, "tf-removed:Vendor.Thing\r\ntf-failed:Vendor.Gone\r\n"));

        var result = new BuiltInAppService(tool, Catalog).Remove(["Vendor.Thing", "Vendor.Gone"]);

        Assert.Equal(["Vendor.Thing"], result.Removed);
        Assert.Equal(["Vendor.Gone"], result.Failed);
        var command = tool.Calls.Single().Arguments[^1];
        Assert.Contains("'Vendor.Thing','Vendor.Gone'", command);
        Assert.Contains("Remove-AppxProvisionedPackage", command);
    }

    [Fact]
    public void Remove_CountsAppsAsFailedWhenThereIsNoOutput()
    {
        var result = new BuiltInAppService(new FakeTool(new ToolOutput(-1, string.Empty)), Catalog).Remove(["Vendor.Thing"]);

        Assert.Empty(result.Removed);
        Assert.Equal(["Vendor.Thing"], result.Failed);
    }

    [Theory]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("x'; Remove-Item C:\\ -Recurse; '")]
    public void Remove_RefusesAnythingOutsideTheCatalog(string id)
    {
        var tool = new FakeTool();

        Assert.Throws<CatalogRequestException>(() => new BuiltInAppService(tool, Catalog).Remove([id]));
        Assert.Empty(tool.Calls);
    }

    [Fact]
    public void Remove_RefusesABadCatalogName()
    {
        var bad = new BuiltInApp("a'b", "Bad", "n", "Other apps", "optional", null);
        var tool = new FakeTool();

        Assert.Throws<CatalogRequestException>(() => new BuiltInAppService(tool, [bad]).Remove(["a'b"]));
        Assert.Empty(tool.Calls);
    }

    [Fact]
    public void Remove_NeedsAtLeastOneApp()
    {
        Assert.Throws<CatalogRequestException>(() => new BuiltInAppService(new FakeTool(), Catalog).Remove([]));
    }
}

public class WingetTableTests
{
    private const string FullList = """
        Name                       Id                                  Version     Available
        --------------------------------------------------------------------------------------
        AdGuard                    AdGuard.AdGuard                     8.0.5595.0
        Adobe Acrobat (64-bit)     Adobe.Acrobat.Pro                   26.002.21   26.002.22
        Xbox Service               ARP\Machine\X64\{41D9BD44-575B}     4.0.305.0

        Name                       Id                                  Version
        --------------------------------------------------------------------
        AdGuard                    AdGuard.AdGuard                     8.0.5595.0
        Apple Music                MSIX\AppleInc.AppleMusicWin_1.0     1.0
        """;

    [Fact]
    public void Parse_ReadsColumnsAcrossTablesWithoutDuplicates()
    {
        var rows = WingetTable.Parse(FullList.ReplaceLineEndings("\r\n"));

        Assert.Equal(4, rows.Count);
        Assert.Equal(new WingetRow("Adobe Acrobat (64-bit)", "Adobe.Acrobat.Pro", "26.002.21", "26.002.22"), rows[1]);
        Assert.Null(rows[0].Available);
        Assert.Equal(@"ARP\Machine\X64\{41D9BD44-575B}", rows[2].Id);
        Assert.Equal(@"MSIX\AppleInc.AppleMusicWin_1.0", rows[3].Id);
    }

    [Fact]
    public void Parse_HandlesProgressTextFooterAndTranslatedHeadings()
    {
        const string text = "\r  -\r  \\\rName    Id      Version Verfügbar Quelle\r\n"
            + "-------------------------------------------\r\n"
            + "Foo Bar Foo.Bar 1.0     2.0       winget\r\n"
            + "1 upgrades available.\r\n";

        var row = Assert.Single(WingetTable.Parse(text));

        Assert.Equal(new WingetRow("Foo Bar", "Foo.Bar", "1.0", "2.0"), row);
    }

    [Theory]
    [InlineData("")]
    [InlineData("No installed package found matching input criteria.")]
    public void Parse_ReturnsNothingWithoutATable(string text)
    {
        Assert.Empty(WingetTable.Parse(text));
    }
}

public class WingetClientTests
{
    private const string Winget = @"C:\fake\winget.exe";
    private const string Listing = """
        Name          Id                    Version  Available
        ---------------------------------------------------------
        7-Zip         7zip.7zip             24.0     25.0
        WebView2      Microsoft.EdgeWebView 1.0
        Plain App     Vendor.Plain          2.0
        """;

    private const string Updates = """
        Name   Id         Version Available Source
        ------------------------------------------
        7-Zip  7zip.7zip  24.0    25.0      winget
        """;

    private static readonly SoftwareItem[] Software =
    [
        new("7zip.7zip", "7-Zip", "n", "Utilities"),
        new("Other.App", "Other", "n", "Utilities"),
        new("9NKSQGP7F2NH", "WhatsApp", "n", "Communications"),
    ];

    private static (WingetClient Client, FakeTool Tool) Listed(params ToolOutput[] after)
    {
        var tool = new FakeTool(
            [new ToolOutput(0, "v1"), new ToolOutput(0, Updates), new ToolOutput(0, Listing), .. after]);
        var client = new WingetClient(tool, Software, Winget);
        client.ListInstalled();
        return (client, tool);
    }

    [Fact]
    public void Lists_SayWhenWingetIsMissing()
    {
        var tool = new FakeTool(new ToolOutput(-1, string.Empty), new ToolOutput(-1, string.Empty));
        var client = new WingetClient(tool, Software, Winget);

        Assert.False(client.ListSoftware().WingetAvailable);
        Assert.False(client.ListInstalled().WingetAvailable);
    }

    [Fact]
    public void ListSoftware_MarksInstalledApps()
    {
        var tool = new FakeTool(new ToolOutput(0, "v1"), new ToolOutput(0, Listing));

        var items = new WingetClient(tool, Software, Winget).ListSoftware().Items;

        Assert.True(items[0].Installed);
        Assert.False(items[1].Installed);
    }

    [Fact]
    public void ListInstalled_PutsUpdatesFirstAndLocksWhatTheAppNeeds()
    {
        var tool = new FakeTool(new ToolOutput(0, "v1"), new ToolOutput(0, Updates), new ToolOutput(0, Listing));

        var items = new WingetClient(tool, Software, Winget).ListInstalled().Items;

        Assert.Equal("7zip.7zip", items[0].Id);
        Assert.Equal("25.0", items[0].Available);
        Assert.True(items.Single(item => item.Id == "Microsoft.EdgeWebView").Locked);
        Assert.False(items.Single(item => item.Id == "Vendor.Plain").Locked);
    }

    [Fact]
    public void ListInstalled_OnlyAsksTheWingetSourceForUpdates()
    {
        var (_, tool) = Listed();

        Assert.Equal(
            ["list", "--upgrade-available", "--source", "winget", "--accept-source-agreements", "--disable-interactivity"],
            tool.Calls[1].Arguments);
    }

    [Fact]
    public void ListInstalled_AcceptsHavingNoUpdates()
    {
        var tool = new FakeTool(
            new ToolOutput(0, "v1"),
            new ToolOutput(unchecked((int)0x8A15002B), "No installed package found."),
            new ToolOutput(0, Listing));

        var items = new WingetClient(tool, Software, Winget).ListInstalled().Items;

        Assert.All(items, item => Assert.Null(item.Available));
    }

    [Fact]
    public void ListInstalled_ReportsAnEmptyFailedList()
    {
        var tool = new FakeTool(
            new ToolOutput(0, "v1"),
            new ToolOutput(0, Updates),
            new ToolOutput(unchecked((int)0x8A15000F), "Failed when opening source(s)."));

        Assert.Throws<CatalogRequestException>(() => new WingetClient(tool, Software, Winget).ListInstalled());
    }

    [Fact]
    public void ListInstalled_KeepsAListWhenOneSourceFailed()
    {
        var tool = new FakeTool(
            new ToolOutput(0, "v1"),
            new ToolOutput(0, Updates),
            new ToolOutput(unchecked((int)0x8A15003B), Listing));

        Assert.Equal(3, new WingetClient(tool, Software, Winget).ListInstalled().Items.Count);
    }

    [Fact]
    public void Install_UsesExactIdAndTheRightSource()
    {
        var tool = new FakeTool();
        var client = new WingetClient(tool, Software, Winget);

        Assert.True(client.Install("7zip.7zip").Ok);
        Assert.True(client.Install("9NKSQGP7F2NH").Ok);

        Assert.Equal(Winget, tool.Calls[0].Path);
        Assert.Equal(
            ["install", "--id", "7zip.7zip", "--exact", "--source", "winget", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
            tool.Calls[0].Arguments);
        Assert.Contains("msstore", tool.Calls[1].Arguments);
    }

    [Theory]
    [InlineData("Not.InCatalog")]
    [InlineData("--override")]
    public void Install_RefusesAnythingOutsideTheCatalog(string id)
    {
        var tool = new FakeTool();

        Assert.Throws<CatalogRequestException>(() => new WingetClient(tool, Software, Winget).Install(id));
        Assert.Empty(tool.Calls);
    }

    [Fact]
    public void Reinstall_UsesTheStoreIdOrExplainsWhyNot()
    {
        var tool = new FakeTool();
        var client = new WingetClient(tool, Software, Winget);

        Assert.True(client.Reinstall(new BuiltInApp("Microsoft.BingNews", "News", "n", "Microsoft", "recommended", "9WZDNCRFHVFW")).Ok);
        Assert.Contains("9WZDNCRFHVFW", tool.Calls.Single().Arguments);

        var result = client.Reinstall(new BuiltInApp("Vendor.Thing", "Thing", "n", "Other apps", "optional", null));
        Assert.False(result.Ok);
        Assert.Single(tool.Calls);
    }

    [Fact]
    public void UpdateAndUninstall_OnlyAcceptProgramsFromTheLastListing()
    {
        var (client, tool) = Listed();
        var before = tool.Calls.Count;

        Assert.Throws<CatalogRequestException>(() => client.Update("Not.Listed"));
        Assert.Throws<CatalogRequestException>(() => client.Uninstall("--force"));
        Assert.Equal(before, tool.Calls.Count);

        Assert.True(client.Update("7zip.7zip").Ok);
        Assert.Equal("upgrade", tool.Calls[^1].Arguments[0]);
        Assert.Contains("winget", tool.Calls[^1].Arguments);
        Assert.True(client.Uninstall("Vendor.Plain").Ok);
        Assert.Equal("uninstall", tool.Calls[^1].Arguments[0]);
        Assert.Contains("--all-versions", tool.Calls[^1].Arguments);
    }

    [Fact]
    public void Uninstall_RefusesLockedPrograms()
    {
        var (client, tool) = Listed();
        var before = tool.Calls.Count;

        Assert.Throws<CatalogRequestException>(() => client.Uninstall("Microsoft.EdgeWebView"));
        Assert.Equal(before, tool.Calls.Count);
    }

    [Fact]
    public void Install_CallsAnInstalledAppInstalledWhenThereIsNoUpdate()
    {
        var tool = new FakeTool(new ToolOutput(unchecked((int)0x8A15002B), string.Empty));

        Assert.Equal("7-Zip is already installed.", new WingetClient(tool, Software, Winget).Install("7zip.7zip").Message);
    }

    [Theory]
    [InlineData(unchecked((int)0x8A150109), true, true)]
    [InlineData(unchecked((int)0x8A150061), true, false)]
    [InlineData(unchecked((int)0x8A15010D), true, false)]
    [InlineData(unchecked((int)0x8A15002B), true, false)]
    [InlineData(unchecked((int)0x8A15010A), false, true)]
    [InlineData(unchecked((int)0x8A15010C), false, false)]
    [InlineData(unchecked((int)0x8A150107), false, false)]
    [InlineData(-1, false, false)]
    [InlineData(1234, false, false)]
    public void Install_TurnsWingetCodesIntoResults(int code, bool ok, bool restart)
    {
        var tool = new FakeTool(new ToolOutput(code, string.Empty));

        var result = new WingetClient(tool, Software, Winget).Install("7zip.7zip");

        Assert.Equal(ok, result.Ok);
        Assert.Equal(restart, result.RestartNeeded);
    }
}
