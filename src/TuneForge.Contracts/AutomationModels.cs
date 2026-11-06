namespace TuneForge.Contracts;

/// <summary>What the person chooses. Everything that runs on its own starts switched off.</summary>
public sealed record SettingsUpdate(
    bool CloseToTray = false,
    bool StartWithWindows = false,
    bool Notifications = true,
    bool MemoryAuto = false,
    int MemoryThreshold = 85,
    bool CleanAuto = false,
    int CleanDays = 7,
    bool StartupWatch = false);

/// <summary>The choices plus what the app remembers between runs.</summary>
public sealed record AppSettings(
    SettingsUpdate Choices,
    string? LastCleanUtc = null,
    string? LastMemoryUtc = null,
    IReadOnlyList<string>? KnownStartup = null,
    IReadOnlyList<GameProfile>? Profiles = null,
    IReadOnlyList<string>? FocusApps = null,
    ActiveBoost? Boost = null);

public sealed record MemoryResult(long UsedBeforeBytes, long UsedAfterBytes, long TotalBytes, int Processes)
{
    public long FreedBytes => Math.Max(0, UsedBeforeBytes - UsedAfterBytes);
}
