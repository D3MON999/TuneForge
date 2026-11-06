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
    bool StartupWatch = false,
    bool RegistryBackup = false,
    bool CrashLogs = false);

/// <summary>The choices plus what the app remembers between runs.</summary>
public sealed record AppSettings(
    SettingsUpdate Choices,
    string? LastCleanUtc = null,
    string? LastMemoryUtc = null,
    IReadOnlyList<string>? KnownStartup = null,
    IReadOnlyList<GameProfile>? Profiles = null,
    IReadOnlyList<string>? FocusApps = null,
    ActiveBoost? Boost = null,
    string? LastBackupUtc = null,
    IReadOnlyList<string>? IgnoredChecks = null,
    IReadOnlyDictionary<string, string>? IgnoredLevels = null);

public sealed record UpdateStatus(string Current, string? Latest, bool Available, string Message, bool Important = false, string? Notes = null);

/// <summary>Nudge goes up each time an important update comes back on screen, so Later only lasts until then.</summary>
public sealed record UpdatePrompt(UpdateStatus Status, int Nudge);

public sealed record MemoryResult(long UsedBeforeBytes, long UsedAfterBytes, long TotalBytes, int Processes, bool Skipped = false)
{
    public long FreedBytes => Math.Max(0, UsedBeforeBytes - UsedAfterBytes);
}
