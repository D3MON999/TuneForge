using System.Globalization;
using System.Security.Principal;
using TuneForge.Contracts;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;

const string Usage = """
    TuneForge command line

      tuneforge-cli list [--category <name>]
      tuneforge-cli apply <id>... [--preset minimal|standard|advanced] [--no-restore-point]
      tuneforge-cli history
      tuneforge-cli undo <change-number>
      tuneforge-cli cleanup scan
      tuneforge-cli cleanup run <category>...
      tuneforge-cli features
      tuneforge-cli feature <id> on|off
      tuneforge-cli repair <id>

    apply, undo, feature, repair and admin cleanup categories need an administrator terminal.
    """;

using var identity = WindowsIdentity.GetCurrent();
var catalog = TweakCatalog.LoadEmbedded();
var build = Environment.OSVersion.Version.Build;
var registry = new WindowsRegistryStore(identity.User!);
var runner = new WindowsProcessRunner();
var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
var cleanup = new CleanupService(CleanupService.Standard(UserPaths.For(identity.User!)), new WindowsRecycleBin());
var features = new FeatureService(runner, new WmiFeatureStateReader());

try
{
    return args.FirstOrDefault() switch
    {
        "list" => List(args[1..]),
        "history" => History(),
        "apply" => Admin(() => Apply(args[1..])),
        "undo" => Admin(() => Undo(args[1..])),
        "cleanup" => Cleanup(args[1..]),
        "features" => Features(),
        "feature" when args.Length == 3 && args[2] is "on" or "off" =>
            Admin(() => Report(features.Set(new FeatureSetRequest(args[1], args[2] == "on")))),
        "repair" when args.Length == 2 =>
            Admin(() => Report(new RepairService(runner, new WindowsDirectoryMover()).Run(new RepairRequest(args[1])))),
        _ => Help(),
    };
}
catch (CatalogRequestException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

int Report(OperationResult result)
{
    Console.WriteLine(result.Message);
    return result.Ok ? 0 : 1;
}

int Cleanup(string[] options)
{
    if (options.FirstOrDefault() == "scan")
    {
        foreach (var category in cleanup.Scan())
        {
            Console.WriteLine($"{category.Id,-14} {category.Bytes,14} bytes {category.Files,8} files");
        }

        return 0;
    }

    if (options.FirstOrDefault() == "run" && options.Length > 1)
    {
        var result = cleanup.Clean(options[1..], elevated);
        Console.WriteLine($"Freed {result.FreedBytes} bytes from {result.Files} files. {result.Skipped} skipped.");
        return 0;
    }

    return Help();
}

int Features()
{
    foreach (var feature in features.List())
    {
        Console.WriteLine($"{feature.State,-12} {feature.Id}");
    }

    return 0;
}

int Help()
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 0 : 2;
}

int List(string[] options)
{
    var category = options.Length == 2 && options[0] == "--category" ? options[1] : null;
    foreach (var item in new TweakScanner(catalog, registry, build).Scan())
    {
        if (category is null || item.Category == category)
        {
            Console.WriteLine($"{item.State,-12} {item.Risk,-7} {item.Id}");
        }
    }

    return 0;
}

int History()
{
    using var journal = Journal.OpenForReading(JournalLocation.DatabasePath);
    foreach (var set in journal?.ListChangeSets() ?? [])
    {
        var state = set.UndoneUtc is null ? "active" : "undone";
        Console.WriteLine($"{set.Id,5}  {set.CreatedUtc}  {state,-7} {set.TweakIds.Count} tweaks");
    }

    return 0;
}

int Admin(Func<int> action)
{
    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
    {
        Console.Error.WriteLine("Run this from an administrator terminal.");
        return 5;
    }

    return action();
}

TweakEngine OpenEngine(Journal journal) => new(
    catalog,
    registry,
    journal,
    new SystemRestorePointService(),
    identity.User!.Value,
    build,
    typeof(TweakEngine).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

int Apply(string[] options)
{
    var ids = new List<string>();
    var restorePoint = true;
    for (var index = 0; index < options.Length; index++)
    {
        switch (options[index])
        {
            case "--no-restore-point":
                restorePoint = false;
                break;
            case "--preset" when index + 1 < options.Length:
                ids.AddRange(catalog.PresetIds(options[++index]));
                break;
            default:
                ids.Add(options[index]);
                break;
        }
    }

    if (ids.Count == 0)
    {
        return Help();
    }

    JournalLocation.EnsureProtected();
    using var journal = Journal.OpenForWriting(JournalLocation.DatabasePath);
    var result = OpenEngine(journal).Apply(new ApplyRequest(ids, restorePoint));
    foreach (var item in result.Results)
    {
        Console.WriteLine($"{item.Status,-12} {item.Id} {item.Message}");
    }

    ShellRefresh.RunAsync().GetAwaiter().GetResult();
    Console.WriteLine($"Change number {result.ChangeSetId}. Restore point: {result.RestorePoint}.");
    return result.Results.Any(item => item.Status == "failed") ? 1 : 0;
}

int Undo(string[] options)
{
    if (options.Length != 1 || !long.TryParse(options[0], CultureInfo.InvariantCulture, out var changeSetId))
    {
        return Help();
    }

    using var journal = Journal.OpenForWriting(JournalLocation.DatabasePath);
    var result = OpenEngine(journal).Undo(changeSetId);
    foreach (var problem in result.Problems)
    {
        Console.WriteLine(problem);
    }

    ShellRefresh.RunAsync().GetAwaiter().GetResult();
    Console.WriteLine($"Restored {result.Restored} settings.");
    return result.Problems.Count == 0 ? 0 : 1;
}
