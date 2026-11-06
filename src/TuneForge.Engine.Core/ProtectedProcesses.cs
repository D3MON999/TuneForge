namespace TuneForge.Engine.Core;

/// <summary>
/// Anti-cheat and the games it guards. TuneForge never opens, changes, trims or ends these, and
/// anything that would touch other processes stays out of the way while one is running.
/// </summary>
public static class ProtectedProcesses
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "vgc", "vgtray", "VALORANT", "VALORANT-Win64-Shipping", "RiotClientServices", "RiotClientUx", "RiotClientUxRender",
        "LeagueClient", "LeagueClientUx", "League of Legends", "EasyAntiCheat", "EasyAntiCheat_EOS", "EasyAntiCheat_Setup",
        "BEService", "BEService_x64", "BattlEye", "FortniteClient-Win64-Shipping", "cs2", "faceit", "faceitclient", "faceitservice",
        "GameGuard", "nProtect", "xigncode", "PnkBstrA", "PnkBstrB", "RainbowSix", "RainbowSix_BE",
    };

    private static readonly string[] Suffixes = ["_BE", "_EAC", "_EAC_EOS"];

    private static readonly string[] Services = ["vgc", "vgk", "BEService", "EasyAntiCheat", "EasyAntiCheat_EOS", "FACEIT", "PnkBstrA", "PnkBstrB"];

    public static bool IsProtected(string name) =>
        Names.Contains(name) || Suffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    public static bool AnyRunning(IEnumerable<string> running) => running.Any(IsProtected);

    public static bool IsProtectedService(string name) => Services.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static bool IsProtectedStartup(string name, string command) =>
        name.Contains("Vanguard", StringComparison.OrdinalIgnoreCase)
        || command.Contains("vgtray", StringComparison.OrdinalIgnoreCase)
        || command.Contains("Riot Vanguard", StringComparison.OrdinalIgnoreCase);
}
