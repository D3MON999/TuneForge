namespace TuneForge.Contracts;

/// <summary>What happens for one game. Everything starts switched off.</summary>
public sealed record GameBoosts(
    bool HighPriority = false,
    bool HighPerformanceGpu = false,
    bool DisableFullscreenOptimizations = false,
    bool PowerBoost = false,
    bool Focus = false)
{
    public bool AnyWhileRunning => HighPriority || PowerBoost || Focus;
}

public sealed record GameEntry(
    string Id,
    string Title,
    string Launcher,
    string Folder,
    string Executable,
    IReadOnlyList<string> ProcessNames,
    string? AntiCheat,
    GameBoosts Boosts);

public sealed record GameProfile(
    string GameId,
    string Title,
    string Executable,
    IReadOnlyList<string> ProcessNames,
    string? AntiCheat,
    GameBoosts Boosts);

public sealed record GameProfileRequest(string GameId, GameBoosts Boosts);

/// <summary>Written before a boost starts, so a crash can still put everything back.</summary>
public sealed record ActiveBoost(string GameId, string Title, string? PreviousPlan, bool ChangedPlan, bool ChangedToasts, int? PreviousToasts);

public sealed record FpsRequest(string Process, int? Seconds = null);

public sealed record BenchmarkRequest(string GameId, string Label);

public sealed record GameRequest(string GameId);

public sealed record BenchmarkRecord(
    string GameId,
    string Title,
    string Label,
    string TimeUtc,
    double AverageFps,
    double OnePercentLowFps,
    int Frames);

public sealed record PowerPlan(string Id, string Name, bool Active);

public sealed record PowerPlanRequest(string Id);

public sealed record GamingOverview(string ResizeBar, IReadOnlyList<PowerPlan> Plans, IReadOnlyList<string> FocusApps, IReadOnlyList<string> RunningApps);

public sealed record FocusAppsRequest(IReadOnlyList<string> Names);
