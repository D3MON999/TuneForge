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

    apply and undo need an administrator terminal.
    """;

using var identity = WindowsIdentity.GetCurrent();
var catalog = TweakCatalog.LoadEmbedded();
var build = Environment.OSVersion.Version.Build;
var registry = new WindowsRegistryStore(identity.User!);

try
{
    return args.FirstOrDefault() switch
    {
        "list" => List(args[1..]),
        "history" => History(),
        "apply" => Admin(() => Apply(args[1..])),
        "undo" => Admin(() => Undo(args[1..])),
        _ => Help(),
    };
}
catch (CatalogRequestException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
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
