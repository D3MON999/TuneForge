using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

public sealed class DisplayDriverRemoverTests : IDisposable
{
    private const string Display = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private readonly string _root = Directory.CreateTempSubdirectory("tf-gpu").FullName;
    private string Inf => Path.Combine(_root, "INF");
    private string Backup => Path.Combine(_root, "backup");

    public DisplayDriverRemoverTests() => Directory.CreateDirectory(Inf);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Points : IRestorePointService
    {
        public int Made { get; private set; }

        public string Create(string description)
        {
            Made++;
            return "requested";
        }
    }

    /// <summary>Acts like pnputil: an export writes a copy of the package, so the backup can be checked.</summary>
    private sealed class Pnputil : IProcessRunner
    {
        public List<string> Lines { get; } = [];

        public int ExportCode { get; set; }

        public int DeleteCode { get; set; }

        public int Run(string tool, IReadOnlyList<string> arguments, TimeSpan timeLimit)
        {
            Lines.Add($"{tool} {string.Join(' ', arguments)}");
            if (arguments[0] == "/export-driver")
            {
                if (ExportCode == 0)
                {
                    File.WriteAllText(Path.Combine(arguments[2], arguments[1]), "copy");
                }

                return ExportCode;
            }

            return arguments[0] == "/delete-driver" ? DeleteCode : 0;
        }
    }

    private static string Package(string vendorId, string guid = Display) => $"""
        [Version]
        Signature = "$Windows NT$"
        Class     = Display
        ClassGuid = {guid}
        Provider  = %Maker%
        DriverVer = 10/01/2025,32.0.15.8180

        [Models]
        %Card% = Section, PCI\{vendorId}&DEV_2504
        """;

    private void Add(string name, string text) => File.WriteAllText(Path.Combine(Inf, name), text);

    private DisplayDriverRemover Remover(Pnputil runner, Points points, int build = 26100) => new(runner, points, Inf, Backup, build);

    [Fact]
    public void FindsGraphicsDriversByMaker()
    {
        Add("oem12.inf", Package("VEN_10DE"));
        Add("oem3.inf", Package("VEN_8086"));
        Add("oem40.inf", Package("VEN_10DE", "{4d36e96c-e325-11ce-bfc1-08002be10318}"));
        Add("nvdisp.inf", Package("VEN_10DE"));

        var packages = DisplayDriverRemover.Scan(Inf);

        Assert.Equal(["Intel:oem3.inf", "NVIDIA:oem12.inf"], packages.Select(package => $"{package.Vendor}:{package.Inf}"));
        Assert.Equal("32.0.15.8180", packages[1].Version);
        Assert.Equal("2025-10-01", packages[1].Date);
    }

    [Fact]
    public void BacksUpThenRemovesOnlyThatMaker()
    {
        Add("oem12.inf", Package("VEN_10DE"));
        Add("oem13.inf", Package("VEN_10DE"));
        Add("oem3.inf", Package("VEN_8086"));
        var runner = new Pnputil();
        var points = new Points();

        var result = Remover(runner, points).Remove("nvidia");

        Assert.True(result.Ok);
        Assert.True(result.RestartNeeded);
        Assert.Equal(1, points.Made);
        Assert.Equal(2, runner.Lines.Count(line => line.Contains("/export-driver")));
        Assert.Contains("pnputil.exe /delete-driver oem12.inf /uninstall /force", runner.Lines);
        Assert.Contains("pnputil.exe /delete-driver oem13.inf /uninstall /force", runner.Lines);
        Assert.DoesNotContain(runner.Lines, line => line.Contains("oem3.inf"));
        Assert.Equal("NVIDIA", Assert.Single(DisplayDriverRemover.Backups(Backup)).Vendor);
        Assert.True(runner.Lines.FindIndex(line => line.Contains("/delete-driver")) > runner.Lines.FindLastIndex(line => line.Contains("/export-driver")));
    }

    [Fact]
    public void RemovesNothingIfTheBackupFails()
    {
        Add("oem12.inf", Package("VEN_10DE"));
        var runner = new Pnputil { ExportCode = 1 };

        var result = Remover(runner, new Points()).Remove("NVIDIA");

        Assert.False(result.Ok);
        Assert.DoesNotContain(runner.Lines, line => line.Contains("/delete-driver"));
        Assert.Empty(DisplayDriverRemover.Backups(Backup));
    }

    [Fact]
    public void KeepsTheBackupWhenWindowsRefusesToRemove()
    {
        Add("oem12.inf", Package("VEN_10DE"));
        var runner = new Pnputil { DeleteCode = 5 };

        var result = Remover(runner, new Points()).Remove("NVIDIA");

        Assert.False(result.Ok);
        Assert.Single(DisplayDriverRemover.Backups(Backup));
    }

    [Fact]
    public void RefusesOlderWindowsThatCantBackUp()
    {
        Add("oem12.inf", Package("VEN_10DE"));
        var runner = new Pnputil();

        Assert.False(Remover(runner, new Points(), build: 18363).Remove("NVIDIA").Ok);
        Assert.Empty(runner.Lines);
    }

    [Fact]
    public void SaysSoWhenThereIsNothingToRemove()
    {
        var runner = new Pnputil();
        var points = new Points();

        Assert.False(Remover(runner, points).Remove("AMD").Ok);
        Assert.Empty(runner.Lines);
        Assert.Equal(0, points.Made);
    }

    [Theory]
    [InlineData("Microsoft")]
    [InlineData("..\\..\\Windows")]
    [InlineData("")]
    public void OnlyKnownMakersAreAccepted(string vendor)
    {
        var remover = Remover(new Pnputil(), new Points());
        Assert.Throws<CatalogRequestException>(() => remover.Remove(vendor));
        Assert.Throws<CatalogRequestException>(() => remover.Restore(vendor));
        Assert.Throws<CatalogRequestException>(() => remover.DeleteBackup(vendor));
    }

    [Fact]
    public void PutsTheBackupBack()
    {
        Add("oem12.inf", Package("VEN_10DE"));
        var runner = new Pnputil();
        var remover = Remover(runner, new Points());
        remover.Remove("NVIDIA");

        var result = remover.Restore("NVIDIA");

        Assert.True(result.Ok);
        Assert.Contains($"pnputil.exe /add-driver {Path.Combine(Backup, "NVIDIA", "*.inf")} /subdirs /install", runner.Lines);
    }

    [Fact]
    public void RestoreWithoutABackupFails()
    {
        Assert.False(Remover(new Pnputil(), new Points()).Restore("AMD").Ok);
    }

    [Fact]
    public void DeletesTheBackup()
    {
        Add("oem12.inf", Package("VEN_10DE"));
        var remover = Remover(new Pnputil(), new Points());
        remover.Remove("NVIDIA");

        Assert.True(remover.DeleteBackup("NVIDIA").Ok);
        Assert.Empty(DisplayDriverRemover.Backups(Backup));
    }
}
