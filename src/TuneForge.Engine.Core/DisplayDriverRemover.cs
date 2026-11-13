using System.Globalization;
using System.Text.RegularExpressions;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

/// <summary>
/// Removes a graphics driver for a clean reinstall, using Windows' own pnputil. Every package is backed up first,
/// and nothing is removed unless the backup worked, so the old driver can always be put back.
/// </summary>
public sealed partial class DisplayDriverRemover(IProcessRunner runner, IRestorePointService restorePoints, string infFolder, string backupRoot, int osBuild)
{
    public static readonly string[] Vendors = ["NVIDIA", "AMD", "Intel"];

    /// <summary>pnputil learned to export drivers in Windows 10 version 2004.</summary>
    private const int ExportBuild = 19041;
    private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);

    public static string DefaultInfFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");

    public static string DefaultBackupRoot => Path.Combine(JournalLocation.Folder, "driver-backup");

    [GeneratedRegex(@"^oem\d+\.inf$", RegexOptions.IgnoreCase)]
    private static partial Regex PublishedName();

    [GeneratedRegex(@"^\s*DriverVer\s*=\s*([^,\r\n]*),\s*([^\s;\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DriverVer();

    [GeneratedRegex(@"^\s*ClassGuid\s*=\s*(\{[0-9a-f-]+\})", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ClassGuid();

    /// <summary>A driver package is a graphics driver if Windows files it under Display. The maker comes from the cards it lists.</summary>
    public static DriverPackage? ReadInf(string fileName, string text)
    {
        var guid = ClassGuid().Match(text);
        if (!PublishedName().IsMatch(fileName)
            || !guid.Success
            || !guid.Groups[1].Value.Equals(DisplayClass, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var vendor = text.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
            : text.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) ? "AMD"
            : text.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) ? "Intel"
            : null;
        if (vendor is null)
        {
            return null;
        }

        var version = DriverVer().Match(text);
        string? date = version.Success && DateTime.TryParseExact(version.Groups[1].Value.Trim(), "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)
            ? when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;
        return new DriverPackage(fileName.ToLowerInvariant(), vendor, version.Success ? version.Groups[2].Value : string.Empty, date);
    }

    /// <summary>Reads the driver store's own copies. It needs no admin rights and works in every Windows language.</summary>
    public static IReadOnlyList<DriverPackage> Scan(string infFolder)
    {
        if (!Directory.Exists(infFolder))
        {
            return [];
        }

        var packages = new List<DriverPackage>();
        foreach (var file in Directory.EnumerateFiles(infFolder, "oem*.inf"))
        {
            try
            {
                if (ReadInf(Path.GetFileName(file), File.ReadAllText(file)) is { } package)
                {
                    packages.Add(package);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A file Windows is replacing right now is skipped.
            }
        }

        return packages.OrderBy(package => package.Vendor).ThenBy(package => package.Inf, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<DriverBackup> Backups(string backupRoot) =>
        Vendors
            .Select(vendor => new DirectoryInfo(Path.Combine(backupRoot, vendor)))
            .Where(folder => folder.Exists && folder.EnumerateFiles("*.inf", SearchOption.AllDirectories).Any())
            .Select(folder => new DriverBackup(folder.Name, folder.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length)))
            .ToList();

    private static string CheckVendor(string vendor) =>
        Vendors.FirstOrDefault(known => known.Equals(vendor, StringComparison.OrdinalIgnoreCase))
        ?? throw new CatalogRequestException("Only NVIDIA, AMD and Intel graphics drivers can be removed here.");

    public OperationResult Remove(string vendor)
    {
        vendor = CheckVendor(vendor);
        if (osBuild < ExportBuild)
        {
            return new OperationResult(false, "This needs Windows 10 version 2004 or newer, so the driver can be backed up first.");
        }

        // Read again here rather than trusting the page's list, and only touch names Windows itself gave out.
        var packages = Scan(infFolder).Where(package => package.Vendor == vendor && PublishedName().IsMatch(package.Inf)).ToList();
        if (packages.Count == 0)
        {
            return new OperationResult(false, $"No {vendor} graphics driver is installed.");
        }

        restorePoints.Create("TuneForge: before removing the graphics driver");

        var folder = Path.Combine(backupRoot, vendor);
        var staging = folder + ".new";
        Clear(staging);
        Directory.CreateDirectory(staging);
        foreach (var package in packages)
        {
            var target = Path.Combine(staging, Path.GetFileNameWithoutExtension(package.Inf));
            Directory.CreateDirectory(target);
            if (runner.Run("pnputil.exe", ["/export-driver", package.Inf, target], Limit) != 0)
            {
                Clear(staging);
                return new OperationResult(false, "The driver couldn't be backed up, so nothing was removed. Check there's a few GB free on drive C.");
            }
        }

        // The new backup replaces the old one only once it's complete.
        Clear(folder);
        Directory.Move(staging, folder);

        var removed = 0;
        foreach (var package in packages)
        {
            var code = runner.Run("pnputil.exe", ["/delete-driver", package.Inf, "/uninstall", "/force"], Limit);
            if (code is 0 or 3010)
            {
                removed++;
            }
        }

        if (removed == 0)
        {
            return new OperationResult(false, "Windows wouldn't remove the driver. Nothing changed, and the backup is kept.");
        }

        var count = removed == 1 ? "1 driver package" : $"{removed} driver packages";
        var partly = removed < packages.Count ? $" {packages.Count - removed} couldn't be removed." : string.Empty;
        return new OperationResult(
            true,
            $"Removed {count} for {vendor}.{partly} Your screen now uses Windows' basic driver. Install the new driver now, then restart.",
            true);
    }

    public OperationResult Restore(string vendor)
    {
        vendor = CheckVendor(vendor);
        var folder = Path.Combine(backupRoot, vendor);
        if (!Directory.Exists(folder))
        {
            return new OperationResult(false, $"There's no backup of the {vendor} driver.");
        }

        var code = runner.Run("pnputil.exe", ["/add-driver", Path.Combine(folder, "*.inf"), "/subdirs", "/install"], Limit);
        return code is 0 or 3010
            ? new OperationResult(true, $"The backed-up {vendor} driver is back. Restart to finish.", true)
            : new OperationResult(false, $"Windows couldn't put the {vendor} driver back. You can still install it from {vendor}'s website.");
    }

    public OperationResult DeleteBackup(string vendor)
    {
        vendor = CheckVendor(vendor);
        Clear(Path.Combine(backupRoot, vendor));
        return new OperationResult(true, $"The {vendor} driver backup was deleted.");
    }

    private static void Clear(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
