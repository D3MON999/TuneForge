using System.Globalization;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

public sealed record WingetRow(string Name, string Id, string Version, string? Available);

/// <summary>
/// Reads winget's text tables. Winget has no structured list output and translates its headings,
/// so columns are found by position under the heading row, not by name.
/// </summary>
public static class WingetTable
{
    public static IReadOnlyList<WingetRow> Parse(string text)
    {
        var lines = text.Split('\n').Select(Clean).ToList();
        var rows = new List<WingetRow>();
        for (var index = 1; index < lines.Count; index++)
        {
            if (lines[index].Length < 10 || lines[index].Any(character => character != '-'))
            {
                continue;
            }

            var columns = ColumnStarts(lines[index - 1]);
            if (columns.Count < 3)
            {
                continue;
            }

            for (index++; index < lines.Count && lines[index].Length > columns[2]; index++)
            {
                var row = lines[index];

                // Summary lines under a table don't line up with its columns.
                if (row[columns[1] - 1] != ' ' || row[columns[2] - 1] != ' ' || Cell(row, columns, 1).Contains(' '))
                {
                    continue;
                }

                rows.Add(new WingetRow(
                    Cell(row, columns, 0),
                    Cell(row, columns, 1),
                    Cell(row, columns, 2),
                    columns.Count > 3 ? NullIfEmpty(Cell(row, columns, 3)) : null));
            }

            index--;
        }

        return rows.Where(row => row.Id.Length > 0).DistinctBy(row => row.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Winget redraws its progress on one line, so only the text after the last return counts.</summary>
    private static string Clean(string line)
    {
        var trimmed = line.TrimEnd('\r', ' ');
        return trimmed[(trimmed.LastIndexOf('\r') + 1)..];
    }

    private static List<int> ColumnStarts(string heading)
    {
        var starts = new List<int>();
        for (var index = 0; index < heading.Length; index++)
        {
            // Short tables can leave a single space between headings.
            if (heading[index] != ' ' && (index == 0 || heading[index - 1] == ' '))
            {
                starts.Add(index);
            }
        }

        return starts;
    }

    private static string Cell(string row, List<int> columns, int column)
    {
        var start = columns[column];
        var end = column + 1 < columns.Count ? columns[column + 1] : row.Length;
        return start >= row.Length ? string.Empty : row[start..Math.Min(end, row.Length)].Trim();
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}

/// <summary>
/// Runs winget from the unprivileged app, never from the elevated engine, because winget's path is
/// inside the user's own folder. Installers that need admin rights show their own Windows prompt.
/// </summary>
public sealed class WingetClient(IToolRunner runner, IReadOnlyList<SoftwareItem> software, string? wingetPath = null)
{
    private const int UpdateNotApplicable = unchecked((int)0x8A15002B);
    private const int NoApplicableInstaller = unchecked((int)0x8A150010);
    private const int NoPackagesFound = unchecked((int)0x8A150014);
    private const int PackageAlreadyInstalled = unchecked((int)0x8A150061);
    private const int PackageInUse = unchecked((int)0x8A150101);
    private const int InstallInProgress = unchecked((int)0x8A150102);
    private const int FileInUse = unchecked((int)0x8A150103);
    private const int DiskFull = unchecked((int)0x8A150105);
    private const int NoNetwork = unchecked((int)0x8A150107);
    private const int RestartToFinish = unchecked((int)0x8A150109);
    private const int RestartBeforeInstall = unchecked((int)0x8A15010A);
    private const int CancelledByUser = unchecked((int)0x8A15010C);
    private const int AlreadyInstalled = unchecked((int)0x8A15010D);

    private static readonly TimeSpan QuickLimit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ListLimit = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InstallLimit = TimeSpan.FromMinutes(45);

    /// <summary>Things Windows, winget or TuneForge itself needs. Their names can appear in either column.</summary>
    private static readonly string[] Locked =
    [
        "TuneForge",
        "Microsoft.AppInstaller",
        "Microsoft.DesktopAppInstaller",
        "App Installer",
        "Microsoft.WindowsStore",
        "Microsoft Store",
        "WebView2",
        "Microsoft Edge",
        "Microsoft.Edge",
        "Microsoft.VCLibs",
        "Microsoft.VCRedist",
        "Visual C++",
        "Microsoft.UI.Xaml",
        "Microsoft.WindowsAppRuntime",
        "Microsoft.NET",
        "Microsoft.DotNet",
        "Microsoft .NET",
        "Microsoft.DirectX",
    ];

    private readonly string _winget = wingetPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft",
        "WindowsApps",
        "winget.exe");

    private volatile IReadOnlyDictionary<string, InstalledProgram> _known = new Dictionary<string, InstalledProgram>();

    public bool Available() => runner.Capture(_winget, ["--version"], QuickLimit).ExitCode == 0;

    public SoftwareList ListSoftware()
    {
        if (!Available())
        {
            return new SoftwareList(false, []);
        }

        var installed = Rows(["list"]).Select(row => row.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new SoftwareList(
            true,
            software.Select(item => new SoftwareEntry(item.Id, item.Title, item.Note, item.Category, installed.Contains(item.Id))).ToList());
    }

    public InstalledList ListInstalled()
    {
        if (!Available())
        {
            return new InstalledList(false, []);
        }

        // Store apps update themselves, and a Store outage shouldn't hide or block everything else.
        var updates = Rows(["list", "--upgrade-available", "--source", "winget"])
            .ToDictionary(row => row.Id, row => row.Available, StringComparer.OrdinalIgnoreCase);
        var programs = Rows(["list"])
            .Select(row => new InstalledProgram(row.Name, row.Id, row.Version, updates.GetValueOrDefault(row.Id), IsLocked(row)))
            .OrderBy(program => program.Available is null)
            .ThenBy(program => program.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _known = programs.ToDictionary(program => program.Id, StringComparer.OrdinalIgnoreCase);
        return new InstalledList(true, programs);
    }

    public OperationResult Install(string id)
    {
        var item = software.FirstOrDefault(entry => entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new CatalogRequestException($"Unknown app: {id}");
        return RunInstall(item.Id, IsStoreId(item.Id) ? "msstore" : "winget", item.Title);
    }

    public OperationResult Reinstall(BuiltInApp app) =>
        app.StoreId is null
            ? new OperationResult(false, $"{app.Title} can't be reinstalled automatically. Look for it in the Microsoft Store.")
            : RunInstall(app.StoreId, "msstore", app.Title);

    public OperationResult Update(string id)
    {
        var program = Known(id);
        var code = runner.Capture(
            _winget,
            ["upgrade", "--id", program.Id, "--exact", "--source", "winget", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
            InstallLimit).ExitCode;
        return Describe(code, program.Name, "updated");
    }

    public OperationResult Uninstall(string id)
    {
        var program = Known(id);
        if (program.Locked)
        {
            throw new CatalogRequestException($"{program.Name} is locked because Windows or TuneForge needs it.");
        }

        var code = runner.Capture(
            _winget,
            ["uninstall", "--id", program.Id, "--exact", "--all-versions", "--silent", "--accept-source-agreements", "--disable-interactivity"],
            InstallLimit).ExitCode;
        return Describe(code, program.Name, "uninstalled");
    }

    private OperationResult RunInstall(string id, string source, string title)
    {
        var code = runner.Capture(
            _winget,
            ["install", "--id", id, "--exact", "--source", source, "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
            InstallLimit).ExitCode;
        return Describe(code, title, "installed");
    }

    /// <summary>Only programs from the last listing can be changed, so the interface can't name anything else.</summary>
    private InstalledProgram Known(string id) =>
        _known.TryGetValue(id, out var program) ? program : throw new CatalogRequestException($"Unknown program: {id}");

    private List<WingetRow> Rows(string[] command)
    {
        var output = runner.Capture(_winget, [.. command, "--accept-source-agreements", "--disable-interactivity"], ListLimit);
        if (output.ExitCode == -1)
        {
            throw new CatalogRequestException("winget didn't answer in time.");
        }

        // A failed source can still leave a usable list, so only an empty one counts as failure.
        var rows = WingetTable.Parse(output.Text).ToList();
        if (rows.Count == 0 && output.ExitCode is not (0 or UpdateNotApplicable or NoPackagesFound))
        {
            throw new CatalogRequestException($"winget couldn't read the app list (code 0x{output.ExitCode.ToString("X8", CultureInfo.InvariantCulture)}).");
        }

        return rows;
    }

    private static bool IsStoreId(string id) =>
        id.Length == 12 && id.All(character => char.IsAsciiDigit(character) || char.IsAsciiLetterUpper(character));

    private static bool IsLocked(WingetRow row) => Locked.Any(name =>
        row.Id.Contains(name, StringComparison.OrdinalIgnoreCase) || row.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    private static OperationResult Describe(int code, string title, string done) => code switch
    {
        0 => new OperationResult(true, $"{title} was {done}."),
        RestartToFinish => new OperationResult(true, $"{title} was {done}. Restart to finish.", true),
        PackageAlreadyInstalled or AlreadyInstalled => new OperationResult(true, $"{title} is already installed."),
        UpdateNotApplicable when done == "installed" => new OperationResult(true, $"{title} is already installed."),
        UpdateNotApplicable => new OperationResult(true, $"No update for {title} fits this PC."),
        RestartBeforeInstall => new OperationResult(false, $"Restart, then try {title} again.", true),
        CancelledByUser => new OperationResult(false, $"{title} was cancelled."),
        PackageInUse or FileInUse => new OperationResult(false, $"{title} is in use. Close it and try again."),
        InstallInProgress => new OperationResult(false, "Another installer is running. Wait for it to finish and try again."),
        DiskFull => new OperationResult(false, $"There isn't enough disk space for {title}."),
        NoNetwork => new OperationResult(false, "Check your internet connection and try again."),
        NoPackagesFound or NoApplicableInstaller => new OperationResult(false, $"winget has no version of {title} for this PC."),
        -1 => new OperationResult(false, $"{title} took too long, or winget couldn't start."),
        _ => new OperationResult(false, $"{title} failed (code 0x{code.ToString("X8", CultureInfo.InvariantCulture)})."),
    };
}
