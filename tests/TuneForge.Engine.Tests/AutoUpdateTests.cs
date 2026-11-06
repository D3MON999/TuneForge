using System.Security.Cryptography;
using System.Text;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

public sealed class AutoUpdateTests : IDisposable
{
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("installer contents");
    private readonly string _folder = Directory.CreateTempSubdirectory("tf-auto").FullName;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly List<string> _launched = [];

    private string Marker => Path.Combine(_folder, "update-starting");

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private sealed class Feed(string version, byte[] signature) : IReleaseFeed
    {
        public ReleaseInfo? Latest() => new(new Version(version), "https://example/setup.exe", "https://example/setup.exe.sig");

        public void Download(string url, string path) => File.WriteAllBytes(path, Installer);

        public byte[] DownloadBytes(string url) => signature;
    }

    private AutoUpdater Updater(string latest, byte[]? signature = null, IReadOnlyList<string>? running = null, Action<string>? launch = null) =>
        new(
            new UpdateService(new Feed(latest, signature ?? _key.SignData(Installer, HashAlgorithmName.SHA256)), new Version("1.0.0"), _key.ExportSubjectPublicKeyInfoPem(), Path.Combine(_folder, "Updates")),
            () => running ?? ["explorer"],
            Marker,
            launch ?? _launched.Add,
            TimeSpan.Zero);

    [Fact]
    public void InstallsASignedNewerVersion()
    {
        Assert.Equal(AutoUpdateResult.Started, Updater("1.1.0").Run());
        Assert.Single(_launched);
        Assert.Equal("1.1.0", File.ReadAllText(Marker));
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.9.0")]
    public void NeverInstallsTheSameOrAnOlderVersion(string version)
    {
        Assert.Equal(AutoUpdateResult.UpToDate, Updater(version).Run());
        Assert.Empty(_launched);
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public void NeverRunsABadSignature()
    {
        Assert.Throws<CatalogRequestException>(() => Updater("1.1.0", _key.SignData([1, 2, 3], HashAlgorithmName.SHA256)).Run());

        Assert.Empty(_launched);
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public void WaitsWhileAGameIsRunning()
    {
        Assert.Equal(AutoUpdateResult.Busy, Updater("1.1.0", running: ["explorer", "VALORANT-Win64-Shipping"]).Run());
        Assert.Empty(_launched);
    }

    [Fact]
    public void WaitsWhileAnotherEngineIsWorking()
    {
        Assert.Equal(AutoUpdateResult.Busy, Updater("1.1.0", running: ["TuneForge.Engine", "TuneForge.Engine"]).Run());
    }

    [Fact]
    public void ItsOwnEngineDoesNotCountAsBusy()
    {
        Assert.Equal(AutoUpdateResult.Started, Updater("1.1.0", running: ["TuneForge.Engine"]).Run());
    }

    [Theory]
    [InlineData("vgc")]
    [InlineData("vgtray")]
    [InlineData("EasyAntiCheat_EOS")]
    [InlineData("BEService")]
    public void AlwaysOnAntiCheatDoesNotBlockUpdatesForever(string name)
    {
        Assert.Equal(AutoUpdateResult.Started, Updater("1.1.0", running: [name]).Run());
    }

    [Fact]
    public void AFailedLaunchLeavesNoMarker()
    {
        var updater = Updater("1.1.0", launch: _ => throw new IOException("no"));

        Assert.Throws<IOException>(() => updater.Run());
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public void TheInstallerArgumentsStaySilentAndDoNotOpenTheAppAsSystem()
    {
        Assert.Contains("/VERYSILENT", AutoUpdater.InstallerArguments);
        Assert.Contains("/autoupdate=1", AutoUpdater.InstallerArguments);
    }
}
