using System.Globalization;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

/// <summary>Lists startup programs and turns them on or off the same way Task Manager does.</summary>
public sealed class StartupService(IRegistryStore registry, UserPaths user)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    // Windows stores on and off in the first byte. The rest stays zero.
    private const string EnabledData = "020000000000000000000000";
    private const string DisabledData = "030000000000000000000000";

    private static readonly Source[] Sources =
    [
        new("hkcu-run", "HKCU", ApprovedKey + "Run", RunKey, null),
        new("hklm-run", "HKLM", ApprovedKey + "Run", RunKey, null),
        new("hklm-run32", "HKLM", ApprovedKey + "Run32", Run32Key, null),
        new("user-folder", "HKCU", ApprovedKey + "StartupFolder", null, Folder.User),
        new("common-folder", "HKLM", ApprovedKey + "StartupFolder", null, Folder.Common),
    ];

    private enum Folder
    {
        User,
        Common,
    }

    public IReadOnlyList<StartupEntry> List() => Sources.SelectMany(ReadSource).ToList();

    /// <summary>Builds the change for a real startup entry. Names that don't exist are refused.</summary>
    public TweakItem BuildChange(StartupSetRequest request)
    {
        var source = Sources.FirstOrDefault(candidate => candidate.Id == request.Source)
            ?? throw new CatalogRequestException("Unknown startup location.");
        if (!ReadSource(source).Any(entry => entry.Name == request.Name))
        {
            throw new CatalogRequestException("That startup item no longer exists.");
        }

        return new TweakItem(
            $"startup:{request.Name}",
            request.Name,
            "startup",
            null,
            "low",
            null,
            "signout",
            [],
            null,
            null,
            "TuneForge",
            [
                new RegistryAction(
                    source.Hive,
                    source.ApprovedPath,
                    request.Name,
                    "binary",
                    System.Text.Json.JsonSerializer.SerializeToElement(request.Enabled ? EnabledData : DisabledData)),
            ]);
    }

    private IEnumerable<StartupEntry> ReadSource(Source source)
    {
        var items = source.Folder is { } folder ? ReadFolder(folder) : ReadRunKey(source);
        return items.Select(item => new StartupEntry(
            source.Id,
            item.Name,
            item.Command,
            IsEnabled(registry.Read(source.Hive, source.ApprovedPath, item.Name)),
            source.Hive == "HKLM"));
    }

    private IEnumerable<(string Name, string Command)> ReadRunKey(Source source) =>
        registry.ValueNames(source.Hive, source.RunPath!)
            .Where(name => name.Length > 0)
            .Select(name => (name, registry.Read(source.Hive, source.RunPath!, name)?.Data ?? string.Empty));

    private IEnumerable<(string Name, string Command)> ReadFolder(Folder folder)
    {
        var root = folder == Folder.User
            ? Path.Combine(user.Roaming, "Microsoft", "Windows", "Start Menu", "Programs", "Startup")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", "Startup");

        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root)
                .Where(path => !Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                .Select(path => (Path.GetFileName(path), path))
            : [];
    }

    private static bool IsEnabled(RegistryValue? approved)
    {
        if (approved is null || approved.Kind != "binary" || approved.Data.Length < 2)
        {
            return true;
        }

        return (byte.Parse(approved.Data.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) & 1) == 0;
    }

    private sealed record Source(string Id, string Hive, string ApprovedPath, string? RunPath, Folder? Folder);
}
