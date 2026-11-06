using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;
using Xunit;

namespace TuneForge.Engine.Tests;

public sealed class UpdateServiceTests : IDisposable
{
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("installer contents");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tf-update-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private sealed class FakeFeed(ReleaseInfo? release, byte[] installer, byte[] signature) : IReleaseFeed
    {
        public Exception? Failure { get; init; }

        public int Downloads { get; private set; }

        public ReleaseInfo? Latest() => Failure is null ? release : throw Failure;

        public void Download(string url, string path)
        {
            Downloads++;
            File.WriteAllBytes(path, installer);
        }

        public byte[] DownloadBytes(string url) => signature;
    }

    private static ReleaseInfo Release(string version) => new(new Version(version), "https://example/setup.exe", "https://example/setup.exe.sig");

    private byte[] Sign(byte[] data) => _key.SignData(data, HashAlgorithmName.SHA256);

    private UpdateService Service(IReleaseFeed feed, string current = "1.0.0") =>
        new(feed, new Version(current), _key.ExportSubjectPublicKeyInfoPem(), _folder);

    [Fact]
    public void Check_PassesOnAnImportantUpdateAndItsNotes()
    {
        var release = Release("1.2.0") with { Important = true, Notes = "Fixes a crash." };

        var status = Service(new FakeFeed(release, [], [])).Check();

        Assert.True(status.Important);
        Assert.Equal("Fixes a crash.", status.Notes);
    }

    [Fact]
    public void Check_OffersANewerVersion()
    {
        var status = Service(new FakeFeed(Release("1.2.0"), [], [])).Check();

        Assert.True(status.Available);
        Assert.Equal("1.2.0", status.Latest);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.9.0")]
    public void Check_NeverOffersTheSameOrAnOlderVersion(string version)
    {
        var feed = new FakeFeed(Release(version), [], []);

        Assert.False(Service(feed).Check().Available);
        Assert.Throws<CatalogRequestException>(() => Service(feed).Download());
        Assert.Equal(0, feed.Downloads);
    }

    [Fact]
    public void Check_SaysWhenNothingIsPublished()
    {
        Assert.Equal("No update has been published yet.", Service(new FakeFeed(null, [], [])).Check().Message);
    }

    [Fact]
    public void Check_SurvivesNoInternet()
    {
        var status = Service(new FakeFeed(null, [], []) { Failure = new HttpRequestException() }).Check();

        Assert.False(status.Available);
        Assert.Contains("internet", status.Message);
    }

    [Fact]
    public void Download_KeepsACorrectlySignedInstaller()
    {
        using var installer = Service(new FakeFeed(Release("2.0.0"), Installer, Sign(Installer))).Download();

        Assert.Equal(Installer, File.ReadAllBytes(installer.Path));
        Assert.EndsWith("TuneForge-Setup-2.0.0.exe", installer.Path);
    }

    [Fact]
    public void Download_LocksTheInstallerUntilItHasRun()
    {
        string path;
        using (var installer = Service(new FakeFeed(Release("2.0.0"), Installer, Sign(Installer))).Download())
        {
            path = installer.Path;
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(path, "swapped"));
            Assert.ThrowsAny<IOException>(() => File.Delete(path));
        }

        File.WriteAllText(path, "free again");
    }

    [Fact]
    public void Download_RejectsAChangedInstaller()
    {
        var feed = new FakeFeed(Release("2.0.0"), Encoding.UTF8.GetBytes("tampered"), Sign(Installer));

        Assert.Throws<CatalogRequestException>(() => Service(feed).Download());
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public void Download_RejectsTheWrongKey()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var feed = new FakeFeed(Release("2.0.0"), Installer, other.SignData(Installer, HashAlgorithmName.SHA256));

        Assert.Throws<CatalogRequestException>(() => Service(feed).Download());
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public void TheShippedKeyCanOnlyVerify()
    {
        var pem = UpdateService.LoadPublicKey();

        Assert.Contains("PUBLIC KEY", pem);
        Assert.DoesNotContain("PRIVATE", pem);
        using var key = ECDsa.Create();
        key.ImportFromPem(pem);
    }
}

public class GitHubReleaseFeedTests
{
    private const string Base = "https://github.com/D3MON999/TuneForge/releases/download/v1.2.3/";

    private static string Json(string tag, params string[] urls) =>
        $"{{\"tag_name\":\"{tag}\",\"assets\":[{string.Join(',', urls.Select(url => $"{{\"browser_download_url\":\"{url}\"}}"))}]}}";

    [Fact]
    public void ReadsARelease()
    {
        var release = GitHubReleaseFeed.Parse(Json("v1.2.3", Base + "TuneForge-Setup-1.2.3.exe", Base + "TuneForge-Setup-1.2.3.exe.sig"));

        Assert.Equal(new Version(1, 2, 3), release!.Version);
        Assert.EndsWith(".exe", release.InstallerUrl);
        Assert.EndsWith(".sig", release.SignatureUrl);
    }

    [Fact]
    public void IgnoresFilesHostedAnywhereElse()
    {
        Assert.Null(GitHubReleaseFeed.Parse(Json("v1.2.3", "https://evil.example/a.exe", "https://evil.example/a.exe.sig")));
    }

    [Fact]
    public void NeedsASignature()
    {
        Assert.Null(GitHubReleaseFeed.Parse(Json("v1.2.3", Base + "TuneForge-Setup-1.2.3.exe")));
    }

    [Fact]
    public void IgnoresABadVersion()
    {
        Assert.Null(GitHubReleaseFeed.Parse(Json("latest")));
    }
}
