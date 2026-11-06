namespace TuneForge.Contracts;

public sealed record AdvancedEntry(
    string Id,
    string Title,
    string Description,
    string Warning,
    string Risk,
    bool RestartNeeded,
    bool Reversible,
    bool Enabled);

public sealed record AdvancedSetRequest(string Id, bool Enabled);

public sealed record SecurityStatus(
    bool? Antivirus,
    bool? RealTimeProtection,
    bool? ControlledFolderAccess,
    bool? Firewall,
    bool? BitLocker);

public sealed record SecuritySetRequest(string Id);

public sealed record ToolLink(string Id, string Title, string Note);

public sealed record ToolOpenRequest(string Id);

public sealed record ProcessEntry(int Id, string Name, long MemoryBytes, bool HasWindow, bool Locked);

public sealed record ProcessEndRequest(int Id, string Name);

public sealed record DisplayModeInfo(string Name, int CurrentHz, int BestHz);

public sealed record CoreParkingStatus(int? MinimumCoresPercent);
