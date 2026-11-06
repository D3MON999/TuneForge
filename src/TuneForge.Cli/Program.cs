using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
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
      tuneforge-cli builtin list
      tuneforge-cli builtin remove <id>...
      tuneforge-cli software list
      tuneforge-cli software install <id>
      tuneforge-cli programs list
      tuneforge-cli programs update|uninstall <id>
      tuneforge-cli system
      tuneforge-cli live
      tuneforge-cli health
      tuneforge-cli report <file>
      tuneforge-cli errors
      tuneforge-cli ping <host>
      tuneforge-cli tcping <host> <port>
      tuneforge-cli trace <host>
      tuneforge-cli dns status
      tuneforge-cli dns benchmark
      tuneforge-cli dns set <provider>|automatic|previous
      tuneforge-cli dns custom <primary> [secondary]
      tuneforge-cli memory clean
      tuneforge-cli autostart status|on|off
      tuneforge-cli games
      tuneforge-cli power list
      tuneforge-cli power use <id>
      tuneforge-cli power add-ultimate
      tuneforge-cli boost-test <process> <settings-file>
      tuneforge-cli advanced list
      tuneforge-cli advanced on|off <id>
      tuneforge-cli speedtest
      tuneforge-cli limit <program.exe> <mbps>
      tuneforge-cli limit-remove <program.exe>
      tuneforge-cli limits

    apply, undo, feature, repair, builtin remove, dns set, dns custom and admin cleanup categories need an administrator terminal.
    """;

using var identity = WindowsIdentity.GetCurrent();
var catalog = TweakCatalog.LoadEmbedded();
var build = Environment.OSVersion.Version.Build;
var registry = new WindowsRegistryStore(identity.User!);
var runner = new WindowsProcessRunner();
var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
var cleanup = new CleanupService(CleanupService.Standard(UserPaths.For(identity.User!)), new WindowsRecycleBin());
var features = new FeatureService(runner, new WmiFeatureStateReader());
var builtIn = new BuiltInAppService(runner, AppCatalogs.LoadBuiltIn());
var winget = new WingetClient(runner, AppCatalogs.LoadSoftware());
var liveProbe = new WindowsLiveProbe(runner);
var events = new EventService(new WindowsEventSource());
var health = new HealthService(
    new WindowsStorageReader(),
    events,
    new StartupService(registry, UserPaths.For(identity.User!)),
    cleanup,
    liveProbe);
var network = new NetworkTools(new WindowsNetworkProbe());
var dnsAdapters = new WindowsDnsAdapters();
var jsonOptions = new JsonSerializerOptions(RpcProtocol.Json) { WriteIndented = true };

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
        "builtin" when args.Length == 2 && args[1] == "list" => ListBuiltIn(),
        "builtin" when args.Length > 2 && args[1] == "remove" => Admin(() => RemoveBuiltIn(args[2..])),
        "software" when args.Length == 2 && args[1] == "list" => ListSoftware(),
        "software" when args.Length == 3 && args[1] == "install" => Report(winget.Install(args[2])),
        "programs" when args.Length == 2 && args[1] == "list" => ListPrograms(),
        "programs" when args.Length == 3 && args[1] == "update" => ChangeProgram(winget.Update, args[2]),
        "programs" when args.Length == 3 && args[1] == "uninstall" => ChangeProgram(winget.Uninstall, args[2]),
        "system" => Show(SystemInfoReader.Read()),
        "live" => Live(),
        "health" => Show(health.Check()),
        "report" when args.Length == 2 => SaveReport(args[1]),
        "errors" => Show(EventService.Group(events.RecentErrors()).Take(20)),
        "ping" when args.Length == 2 => Show(network.Ping(new ProbeRequest(args[1]))),
        "tcping" when args.Length == 3 && int.TryParse(args[2], out var port) => Show(network.TcpPing(new ProbeRequest(args[1], port))),
        "trace" when args.Length == 2 => Show(network.Trace(new ProbeRequest(args[1]))),
        "dns" when args.Length == 2 && args[1] == "status" => Show(dnsAdapters.ReadCurrent()),
        "dns" when args.Length == 2 && args[1] == "benchmark" => Show(network.BenchmarkDns(dnsAdapters.ReadCurrent())),
        "dns" when args.Length == 3 && args[1] == "set" => Admin(() => SetDns(
            args[2] is "automatic" or "previous" ? new DnsSetRequest(args[2]) : new DnsSetRequest("preset", args[2]))),
        "dns" when args.Length >= 3 && args[1] == "custom" => Admin(() => SetDns(
            new DnsSetRequest("custom", null, args[2], args.ElementAtOrDefault(3)))),
        "memory" when args.Length == 2 && args[1] == "clean" => Show(new WindowsMemoryTrimmer(liveProbe).Trim()),
        "autostart" when args.Length == 2 && args[1] is "status" or "on" or "off" => Autostart(args[1]),
        "games" => Show(new GameScanner(GameSources.Detect()).Scan([])),
        "power" when args.Length == 2 && args[1] == "list" => Show(new WindowsPowerPlans(runner).List()),
        "power" when args.Length == 3 && args[1] == "use" => Report(new OperationResult(new WindowsPowerPlans(runner).Activate(args[2]), "Done.")),
        "power" when args.Length == 2 && args[1] == "add-ultimate" => Report(new OperationResult(new WindowsPowerPlans(runner).AddUltimate(), "Done.")),
        "boost-test" when args.Length == 3 => BoostTest(args[1], args[2]),
        "advanced" when args.Length == 2 && args[1] == "list" => Show(Advanced().List()),
        "advanced" when args.Length == 3 && args[1] is "on" or "off" => Admin(() => Report(Advanced().Set(new AdvancedSetRequest(args[2], args[1] == "on")))),
        "speedtest" => Speed(),
        "limit" when args.Length == 3 && long.TryParse(args[2], out var mbps) => Admin(() => Report(new BandwidthLimits(runner).Set(new BandwidthRule(args[1], mbps)))),
        "limit-remove" when args.Length == 2 => Admin(() => Report(new BandwidthLimits(runner).Remove(args[1]))),
        "limits" => Show(new BandwidthLimits(runner).List()),
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

int Show<T>(T value)
{
    Console.WriteLine(JsonSerializer.Serialize(value, jsonOptions));
    return 0;
}

int Live()
{
    var monitor = new LiveMonitor(liveProbe);
    monitor.Sample();
    Thread.Sleep(2000);
    return Show(monitor.Sample());
}

int SaveReport(string path)
{
    File.WriteAllText(path, health.BuildReport());
    Console.WriteLine($"Saved {path}");
    return 0;
}

int SetDns(DnsSetRequest request)
{
    JournalLocation.EnsureProtected();
    return Report(new DnsService(runner, dnsAdapters, new FileDnsBackup(JournalLocation.DnsBackupPath)).Set(request));
}

// Boosts a running process as if it were a game, then undoes everything when it exits.
int BoostTest(string process, string settingsFile)
{
    var plans = new WindowsPowerPlans(runner);
    var gameSettings = new WindowsGameSettings();
    var apps = new WindowsRunningApps();
    var booster = new GameBooster(new SettingsStore(settingsFile), gameSettings, plans, apps, (_, message) => Console.WriteLine(message));
    var executable = $@"C:\TuneForgeTest\{process}.exe";
    void State(string label)
    {
        using var running = System.Diagnostics.Process.GetProcessesByName(process).FirstOrDefault();
        Console.WriteLine($"{label}: plan={plans.List().FirstOrDefault(plan => plan.Active)?.Name} toasts={gameSettings.ReadToasts()?.ToString() ?? "unset"} priority={running?.PriorityClass.ToString() ?? "none"} gpu={gameSettings.ReadGpu(executable) ?? "unset"} fullscreen={gameSettings.ReadFullscreen(executable) ?? "unset"}");
    }

    var game = new GameEntry("test", process, "Test", @"C:\TuneForgeTest", executable, [process], null, new GameBoosts());
    State("before");
    booster.Save(game, new GameBoosts(true, true, true, true, true));
    booster.Tick();
    State("running");
    while (apps.ProcessNames().Contains(process))
    {
        Thread.Sleep(500);
    }

    booster.Tick();
    booster.Save(game, new GameBoosts());
    State("after");
    return 0;
}

AdvancedService Advanced()
{
    if (elevated)
    {
        JournalLocation.EnsureProtected();
    }

    return new AdvancedService(runner, new FileAdvancedState(JournalLocation.AdvancedStatePath), new WindowsAdapterSettings());
}

int Speed()
{
    using var probe = new CloudflareSpeedProbe();
    return Show(new SpeedTest(probe).Run());
}

int Autostart(string action)
{
    var autostart = new WindowsAutostart();
    if (action != "status")
    {
        autostart.Set(action == "on", Path.Combine(AppContext.BaseDirectory, "TuneForge.exe"));
    }

    Console.WriteLine(autostart.Enabled ? "on" : "off");
    return 0;
}

int ListBuiltIn()
{
    foreach (var app in builtIn.List())
    {
        Console.WriteLine($"{(app.Installed ? "installed" : "removed"),-10} {app.Safety,-12} {app.Id}");
    }

    return 0;
}

int RemoveBuiltIn(string[] ids)
{
    var result = builtIn.Remove(ids);
    foreach (var id in result.Removed)
    {
        Console.WriteLine($"removed {id}");
    }

    foreach (var id in result.Failed)
    {
        Console.WriteLine($"failed {id}");
    }

    return result.Failed.Count == 0 ? 0 : 1;
}

int ListSoftware()
{
    var list = winget.ListSoftware();
    if (!list.WingetAvailable)
    {
        Console.Error.WriteLine("winget isn't available.");
        return 1;
    }

    foreach (var item in list.Items)
    {
        Console.WriteLine($"{(item.Installed ? "installed" : "available"),-10} {item.Id}");
    }

    return 0;
}

int ListPrograms()
{
    var list = winget.ListInstalled();
    if (!list.WingetAvailable)
    {
        Console.Error.WriteLine("winget isn't available.");
        return 1;
    }

    foreach (var item in list.Items)
    {
        Console.WriteLine($"{(item.Locked ? "locked" : "open"),-7} {item.Available ?? "current",-16} {item.Id}");
    }

    return 0;
}

int ChangeProgram(Func<string, OperationResult> change, string id)
{
    winget.ListInstalled();
    return Report(change(id));
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
