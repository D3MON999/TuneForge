using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

public sealed record ReleaseInfo(Version Version, string InstallerUrl, string SignatureUrl);

public interface IReleaseFeed
{
    /// <summary>The newest release, or null if none has been published.</summary>
    ReleaseInfo? Latest();

    void Download(string url, string path);

    byte[] DownloadBytes(string url);
}

/// <summary>Only talks to the app's own public releases, and only when someone asks.</summary>
public sealed class GitHubReleaseFeed : IReleaseFeed, IDisposable
{
    public const string Repository = "D3MON999/TuneForge-Releases";
    private const long MaxBytes = 1024L * 1024 * 1024;
    private static readonly string LatestUrl = $"https://api.github.com/repos/{Repository}/releases/latest";
    private static readonly string DownloadPrefix = $"https://github.com/{Repository}/releases/download/";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public GitHubReleaseFeed() => _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TuneForge", "1"));

    public ReleaseInfo? Latest()
    {
        using var response = _http.GetAsync(LatestUrl).GetAwaiter().GetResult();
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }

    public static ReleaseInfo? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var tag = document.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v');
        if (!Version.TryParse(tag, out var version))
        {
            return null;
        }

        var urls = document.RootElement.GetProperty("assets").EnumerateArray()
            .Select(asset => asset.GetProperty("browser_download_url").GetString() ?? string.Empty)
            .Where(url => url.StartsWith(DownloadPrefix, StringComparison.Ordinal))
            .ToList();
        var installer = urls.FirstOrDefault(url => url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        var signature = urls.FirstOrDefault(url => url.EndsWith(".exe.sig", StringComparison.OrdinalIgnoreCase));
        return installer is null || signature is null ? null : new ReleaseInfo(version, installer, signature);
    }

    public void Download(string url, string path)
    {
        using var response = _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using var source = response.Content.ReadAsStream();
        using var target = File.Create(path);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            total += read;
            if (total > MaxBytes)
            {
                throw new IOException("The download is too large.");
            }

            target.Write(buffer, 0, read);
        }
    }

    public byte[] DownloadBytes(string url) => _http.GetByteArrayAsync(url).GetAwaiter().GetResult();

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Installers run with admin rights, so nothing runs unless it carries a signature from the
/// release key. The app holds only the public half, which can check but not sign.
/// </summary>
public sealed class UpdateService(IReleaseFeed feed, Version current, string publicKeyPem, string folder)
{
    public const string SilentArguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART";

    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TuneForge",
        "Updates");

    public static string LoadPublicKey()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TuneForge.ReleaseKey.pem")
            ?? throw new InvalidDataException("The release key is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Text(Version version) => version.ToString(3);

    public UpdateStatus Check()
    {
        ReleaseInfo? latest;
        try
        {
            latest = feed.Latest();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new UpdateStatus(Text(current), null, false, "Updates couldn't be checked. Check your internet connection and try again.");
        }

        if (latest is null)
        {
            return new UpdateStatus(Text(current), null, false, "No update has been published yet.");
        }

        return Newer(latest)
            ? new UpdateStatus(Text(current), Text(latest.Version), true, $"Version {Text(latest.Version)} is ready to install.")
            : new UpdateStatus(Text(current), Text(latest.Version), false, "TuneForge is up to date.");
    }

    /// <summary>Returns the path of a verified installer, or throws without leaving one behind.</summary>
    public string Download()
    {
        var latest = feed.Latest() ?? throw new CatalogRequestException("No update has been published yet.");
        if (!Newer(latest))
        {
            throw new CatalogRequestException("TuneForge is up to date.");
        }

        Directory.CreateDirectory(folder);
        foreach (var old in Directory.EnumerateFiles(folder))
        {
            File.Delete(old);
        }

        var path = Path.Combine(folder, $"TuneForge-Setup-{Text(latest.Version)}.exe");
        feed.Download(latest.InstallerUrl, path);
        var signature = feed.DownloadBytes(latest.SignatureUrl);
        if (!Verify(path, signature))
        {
            File.Delete(path);
            throw new CatalogRequestException("The update failed its signature check, so it wasn't installed.");
        }

        return path;
    }

    private bool Newer(ReleaseInfo release) => release.Version > current;

    private bool Verify(string path, byte[] signature)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        using var stream = File.OpenRead(path);
        return key.VerifyData(stream, signature, HashAlgorithmName.SHA256);
    }
}
