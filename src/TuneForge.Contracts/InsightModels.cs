namespace TuneForge.Contracts;

/// <summary>A folder or file and how much space it takes, counting everything inside a folder.</summary>
public sealed record SpaceEntry(string Name, string Path, long Bytes, bool IsFolder);

public sealed record SpaceReport(string Path, long Bytes, IReadOnlyList<SpaceEntry> Items, IReadOnlyList<SpaceEntry> LargestFiles);

public sealed record SpaceRequest(string Path);

/// <summary>Version is what Windows reports. FriendlyVersion is the number the maker uses, when it can be worked out.</summary>
public sealed record GraphicsDriver(string Name, string Vendor, string Version, string? FriendlyVersion, string? DateUtc, int? AgeDays, bool HasDownloadPage);

public sealed record DriverPageRequest(string Vendor);

/// <summary>Windows' own measurements of one start, in milliseconds.</summary>
public sealed record BootRecord(string WhenUtc, int TotalMs, int MainPathMs, int AfterSignInMs);

/// <summary>FirstChangeUtc is TuneForge's first recorded change, so starts can be compared before and after it.</summary>
public sealed record BootReport(IReadOnlyList<BootRecord> Boots, string? FirstChangeUtc);
