using System.Text;
using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

/// <summary>
/// Lists and removes built-in Windows apps. Only apps in the catalog can be removed, and each name
/// is checked again here because it goes into a PowerShell command.
/// </summary>
public sealed class BuiltInAppService(IToolRunner runner, IReadOnlyList<BuiltInApp> catalog)
{
    private const string Done = "tf-removed:";
    private const string NotDone = "tf-failed:";
    private static readonly TimeSpan ListLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RemoveLimit = TimeSpan.FromMinutes(30);

    private static string PowerShell =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>Apps that are installed, plus removed ones that can come back from the Store.</summary>
    public IReadOnlyList<BuiltInAppEntry> List()
    {
        var output = runner.Capture(
            PowerShell,
            ["-NoProfile", "-NonInteractive", "-Command", "Get-AppxPackage | ForEach-Object { $_.Name }"],
            ListLimit);
        if (output.ExitCode != 0)
        {
            throw new CatalogRequestException("Windows couldn't list its apps.");
        }

        var installed = output.Text
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return catalog
            .Where(app => installed.Contains(app.Id) || app.StoreId is not null)
            .Select(app => new BuiltInAppEntry(
                app.Id,
                app.Title,
                app.Note,
                app.Group,
                app.Safety,
                installed.Contains(app.Id),
                app.StoreId is not null))
            .ToList();
    }

    /// <summary>Removes the apps for every user, and from the copy Windows gives new users.</summary>
    public BuiltInRemoveResult Remove(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
        {
            throw new CatalogRequestException("No apps were selected.");
        }

        var apps = ids.Distinct(StringComparer.OrdinalIgnoreCase).Select(Find).ToList();
        var output = runner.Capture(
            PowerShell,
            ["-NoProfile", "-NonInteractive", "-Command", BuildRemoveScript(apps)],
            RemoveLimit);

        var lines = output.Text.Split('\n', StringSplitOptions.TrimEntries);
        var removed = apps.Where(app => lines.Contains(Done + app.Id)).Select(app => app.Id).ToList();
        return new BuiltInRemoveResult(removed, apps.Select(app => app.Id).Except(removed).ToList());
    }

    /// <summary>Asks Windows to register App Installer again, which brings winget back.</summary>
    public OperationResult RepairWinget()
    {
        var output = runner.Capture(
            PowerShell,
            ["-NoProfile", "-NonInteractive", "-Command", "Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe"],
            ListLimit);
        return output.ExitCode == 0
            ? new OperationResult(true, "winget was repaired.")
            : new OperationResult(false, "winget couldn't be repaired. Update App Installer from the Microsoft Store.");
    }

    public BuiltInApp Find(string id)
    {
        var app = catalog.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new CatalogRequestException($"Unknown app: {id}");

        // Catalog names never contain these, so a bad catalog can't reach the command line.
        if (!app.Id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-'))
        {
            throw new CatalogRequestException($"Invalid app name: {id}");
        }

        return app;
    }

    private static string BuildRemoveScript(IEnumerable<BuiltInApp> apps)
    {
        var names = string.Join(',', apps.Select(app => $"'{app.Id}'"));
        return new StringBuilder()
            .Append("$provisioned = @(Get-AppxProvisionedPackage -Online);")
            .Append($"foreach ($name in @({names})) {{")
            .Append("try { Get-AppxPackage -AllUsers -Name $name | Remove-AppxPackage -AllUsers -ErrorAction Stop } catch { };")
            .Append("foreach ($package in @($provisioned | Where-Object { $_.DisplayName -eq $name })) {")
            .Append("try { Remove-AppxProvisionedPackage -Online -AllUsers -PackageName $package.PackageName -ErrorAction Stop | Out-Null } catch { } };")
            .Append($"if (Get-AppxPackage -AllUsers -Name $name) {{ '{NotDone}' + $name }} else {{ '{Done}' + $name }} }}")
            .ToString();
    }
}
