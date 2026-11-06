using System.Diagnostics;
using System.Security.Principal;
using TuneForge.Engine.Core;
using TuneForge.Engine.Core.Catalog;

// Arguments: --pipe <name> --client-sid <sid> --parent-pid <pid>
var arguments = ParseArguments(args);
if (arguments is null)
{
    return 2;
}

var hostPath = Path.Combine(AppContext.BaseDirectory, "TuneForge.exe");
using var stop = new CancellationTokenSource();
using var parent = Process.GetProcessById(arguments.Value.ParentPid);
parent.EnableRaisingEvents = true;
parent.Exited += (_, _) => stop.Cancel();

JournalLocation.EnsureProtected();
using var journal = Journal.OpenForWriting(JournalLocation.DatabasePath);
var tweaks = new TweakEngine(
    TweakCatalog.LoadEmbedded(),
    new WindowsRegistryStore(arguments.Value.ClientSid),
    journal,
    new SystemRestorePointService(),
    arguments.Value.ClientSid.Value,
    Environment.OSVersion.Version.Build,
    typeof(TweakEngine).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

var server = new EngineServer(
    new EngineOptions(
        arguments.Value.Pipe,
        arguments.Value.ClientSid,
        hostPath,
        TimeSpan.FromSeconds(30)),
    tweaks);

try
{
    await server.RunAsync(stop.Token);
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (UnauthorizedAccessException)
{
    return 3;
}

static (string Pipe, SecurityIdentifier ClientSid, int ParentPid)? ParseArguments(string[] values)
{
    if (values.Length != 6 || values[0] != "--pipe" || values[2] != "--client-sid" || values[4] != "--parent-pid")
    {
        return null;
    }

    if (!values[1].StartsWith("TuneForge.Engine.", StringComparison.Ordinal)
        || !int.TryParse(values[5], out var parentPid))
    {
        return null;
    }

    try
    {
        return (values[1], new SecurityIdentifier(values[3]), parentPid);
    }
    catch (ArgumentException)
    {
        return null;
    }
}
