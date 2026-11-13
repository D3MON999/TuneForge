namespace TuneForge.Contracts;

/// <summary>A folder or file and how much space it takes, counting everything inside a folder.</summary>
public sealed record SpaceEntry(string Name, string Path, long Bytes, bool IsFolder);

public sealed record SpaceReport(string Path, long Bytes, IReadOnlyList<SpaceEntry> Items, IReadOnlyList<SpaceEntry> LargestFiles);

public sealed record SpaceRequest(string Path);

/// <summary>Version is what Windows reports. FriendlyVersion is the number the maker uses, when it can be worked out.</summary>
public sealed record GraphicsDriver(string Name, string Vendor, string Version, string? FriendlyVersion, string? DateUtc, int? AgeDays, bool HasDownloadPage);

public sealed record DriverPageRequest(string Vendor);

/// <summary>A graphics driver package in Windows' driver store. Inf is the name Windows gave it, like oem12.inf.</summary>
public sealed record DriverPackage(string Inf, string Vendor, string Version, string? Date);

public sealed record DriverBackup(string Vendor, long Bytes);

public sealed record DriverCleanState(IReadOnlyList<DriverPackage> Packages, IReadOnlyList<DriverBackup> Backups);

/// <summary>Windows' own measurements of one start, in milliseconds.</summary>
public sealed record BootRecord(string WhenUtc, int TotalMs, int MainPathMs, int AfterSignInMs);

/// <summary>An app Windows caught slowing down its start. AddedMs is the average delay it caused.</summary>
public sealed record SlowStartupApp(string Name, string File, int Times, int AddedMs);

/// <summary>What the engine reads from Windows' startup log.</summary>
public sealed record BootDiagnostics(IReadOnlyList<BootRecord> Boots, IReadOnlyList<SlowStartupApp> SlowApps);

/// <summary>FirstChangeUtc is TuneForge's first recorded change, so starts can be compared before and after it.</summary>
public sealed record BootReport(IReadOnlyList<BootRecord> Boots, string? FirstChangeUtc, IReadOnlyList<SlowStartupApp> SlowApps);

/// <summary>Capacities in milliwatt-hours, as Windows' battery report gives them.</summary>
public sealed record BatteryInfo(string? Name, string? Maker, long DesignMwh, long FullMwh, int? Cycles);

public sealed record BatteryReport(IReadOnlyList<BatteryInfo> Batteries);

public sealed record OldDownload(string Name, string Path, long Bytes, string LastUsedUtc, int AgeDays);

public sealed record OldDownloadsReport(string Folder, IReadOnlyList<OldDownload> Files);

public sealed record RecycleRequest(IReadOnlyList<string> Paths);

public sealed record RecycleResult(int Moved, int Failed);
