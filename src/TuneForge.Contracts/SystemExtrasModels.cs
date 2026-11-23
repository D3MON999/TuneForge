namespace TuneForge.Contracts;

/// <summary>What the drive reports about itself. Any value can be missing, because not every drive reports it.</summary>
public sealed record DriveWear(string Name, string Media, int? WearPercent, int? TemperatureC, int? PowerOnHours, long? ReadErrors);

/// <summary>One wake from sleep. Source is what Windows named, like a device, a timer or the power button.</summary>
public sealed record WakeEvent(string WhenUtc, string Source);

/// <summary>A device that can wake the PC. Armed means it's allowed to right now.</summary>
public sealed record WakeDevice(string Name, bool Armed);

public sealed record WakeReport(IReadOnlyList<WakeEvent> Wakes, IReadOnlyList<WakeDevice> Devices);

/// <summary>Kind is what it keeps awake: "display", "system" or "away". Who is a program, driver or service.</summary>
public sealed record SleepBlocker(string Kind, string Who, string Detail);

public sealed record WakeDeviceRequest(string Name, bool Armed);

/// <summary>A scheduled task added by a program. Windows' own tasks aren't listed.</summary>
public sealed record ScheduledTaskInfo(string Path, string Name, string Program, string? Author, bool Enabled, string? NextRunUtc, bool Locked);

public sealed record TaskSetRequest(string Path, bool Enabled);

/// <summary>An entry another program added to the right-click menu. Where is "files", "folders" or "background".</summary>
public sealed record MenuEntry(string Id, string Name, string Where, string? File, bool Enabled);

public sealed record MenuSetRequest(string Id, bool Enabled);

/// <summary>Status is "windows", "signed" or "unsigned". Publisher is the signer's name, when signed.</summary>
public sealed record ProcessSignature(string Name, string Path, string Status, string? Publisher);

public sealed record FirewallBlock(string Program);

public sealed record FirewallRequest(string Program);

public sealed record UpdatePauseStatus(bool Paused, string? UntilUtc);

public sealed record UpdatePauseRequest(int Weeks);

public sealed record DriverExportResult(bool Ok, string Message, string? Folder);

public sealed record FolderRequest(string Folder);
