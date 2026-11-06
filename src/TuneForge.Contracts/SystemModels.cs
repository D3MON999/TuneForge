namespace TuneForge.Contracts;

/// <summary>Outcome of one system job, in words a person can act on.</summary>
public sealed record OperationResult(bool Ok, string Message, bool RestartNeeded = false);

public sealed record CleanupCategoryInfo(
    string Id,
    string Title,
    string Note,
    bool NeedsAdmin,
    bool DefaultOn,
    long Bytes,
    long Files);

public sealed record CleanupRequest(IReadOnlyList<string> Ids);

public sealed record CleanupResult(long FreedBytes, long Files, long Skipped);

public sealed record StartupEntry(string Source, string Name, string Command, bool Enabled, bool NeedsAdmin);

public sealed record StartupSetRequest(string Source, string Name, bool Enabled);

public sealed record ServiceEntry(string Name, string DisplayName, bool Running, string StartType, bool Protected);

public sealed record ServiceSetRequest(string Name, string StartType);

public sealed record FeatureEntry(string Id, string Title, string Note, string State);

public sealed record FeatureSetRequest(string Id, bool Enabled);

public sealed record RepairInfo(string Id, string Title, string Note, bool LongRunning);

public sealed record RepairRequest(string Id);
