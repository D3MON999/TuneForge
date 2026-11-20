namespace TuneForge.Contracts;

// Repair

/// <summary>One crash. Kind is "bluescreen" or "power-loss". Code is the stop code Windows recorded, when there is one.</summary>
public sealed record CrashRecord(string WhenUtc, string Kind, string? Code, string Name, string Meaning, string? DumpPath);

public sealed record SavedRestorePoint(long Sequence, string Description, string CreatedUtc);

public sealed record RestorePointReport(IReadOnlyList<SavedRestorePoint> Points, long UsedBytes, long MaxBytes);

public sealed record RestorePointRequest(long Sequence);

// Network

/// <summary>The speed a network adapter agreed with the router or switch. The Wi-Fi fields are only set for Wi-Fi.</summary>
public sealed record LinkInfo(string Name, string Kind, long SpeedBps, string? Network, int? SignalPercent, int? Channel, string? Band);

public sealed record WifiNetwork(string Name, int Channel, string Band, int SignalPercent, bool Connected);

/// <summary>Problem is set when Windows refused the scan, for example because location access is off.</summary>
public sealed record WifiScan(bool HasWifi, string? Problem, IReadOnlyList<WifiNetwork> Networks);

/// <summary>Where is "router" or "internet". Kind is "drop" or "spike". Ms is the slowest reply in a spike.</summary>
public sealed record ConnectionLogEvent(string WhenUtc, string Where, string Kind, int Seconds, int? Ms);

public sealed record ConnectionLogStatus(
    bool Running,
    string? SinceUtc,
    long Pings,
    string? Router,
    IReadOnlyList<ConnectionLogEvent> Events,
    string LogPath);

public sealed record ConnectionLogRequest(bool Running);

/// <summary>Note is the comment written just above the entry, often naming the program that added it.</summary>
public sealed record HostsEntry(string Text, string Address, IReadOnlyList<string> Names, string? Note, bool Blocks);

public sealed record HostsReport(IReadOnlyList<HostsEntry> Entries, bool HasBackup);

public sealed record HostsRemoveRequest(IReadOnlyList<string> Lines);

// Testers

public sealed record DiskSpeedRequest(string Drive);

public sealed record DiskSpeedResult(string Drive, double WriteMBps, double ReadMBps, double RandomReadIops);

// Cleanup and Apps

/// <summary>Where is "personal" for your own folders, or a drive like "D:\".</summary>
public sealed record DuplicateRequest(string Where);

public sealed record DuplicateFile(string Path, string Name, string ModifiedUtc);

public sealed record DuplicateGroup(long Bytes, IReadOnlyList<DuplicateFile> Files);

public sealed record DuplicateReport(IReadOnlyList<DuplicateGroup> Groups, long WastedBytes);

/// <summary>Keep is the copy that must stay. A file is only removed if Keep still exists and is identical.</summary>
public sealed record DuplicatePick(string Path, string Keep);

public sealed record DuplicateRecycleRequest(IReadOnlyList<DuplicatePick> Picks);

public sealed record LeftoverRequest(string Name);

/// <summary>CanRemove is true for folders in your own profile. Others are only shown.</summary>
public sealed record LeftoverFolder(string Path, long Bytes, bool CanRemove);

public sealed record LeftoverRecycleRequest(string Name, IReadOnlyList<string> Paths);

public sealed record DelayedStartup(string Source, string Name, string Command, int DelaySeconds);

public sealed record StartupDelayRequest(string Source, string Name, bool Delayed);

public sealed record BackgroundApp(string Id, string Name, bool Allowed);

/// <summary>AllBlocked means the one switch for every app is off, so the per-app ones don't matter.</summary>
public sealed record BackgroundAppsReport(bool AllBlocked, IReadOnlyList<BackgroundApp> Apps);

public sealed record BackgroundAppRequest(string Id, bool Allowed);

// Dashboard and Privacy

public sealed record HealthPoint(string Date, int Score);

public sealed record ChangeList(string Category, IReadOnlyList<string> Added, IReadOnlyList<string> Removed);

/// <summary>SinceDate is the day the comparison starts from, or null when there's nothing to compare yet.</summary>
public sealed record ChangesReport(string? SinceDate, IReadOnlyList<ChangeList> Changes);

/// <summary>Capability is "camera", "microphone" or "location".</summary>
public sealed record PermissionUse(string Capability, string App, string LastUsedUtc, bool InUse);

public sealed record SettingsPageRequest(string Page);
