using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

/// <summary>Reads tweak state without changing anything. Safe to run without admin rights.</summary>
public sealed class TweakScanner(TweakCatalog catalog, IRegistryStore registry, int osBuild)
{
    public IReadOnlyList<TweakStatus> Scan() => catalog.Items.Select(ToStatus).ToList();

    public IReadOnlyList<TweakPreview> Preview(IEnumerable<string> ids) =>
        catalog.Resolve(ids).Select(item => new TweakPreview(
            item.Id,
            item.Title,
            item.Restart,
            item.Impact,
            item.Actions
                .Select(action => (action, before: registry.Read(action.Hive, action.Path, action.Name)))
                .Where(pair => !Matches(pair.before, pair.action.Target))
                .Select(pair => new TweakChange(pair.action.Display, Show(pair.before), Show(pair.action.Target)))
                .ToList()))
        .ToList();

    private TweakStatus ToStatus(TweakItem item)
    {
        var state = !item.Supports(osBuild) ? TweakStates.Unsupported
            : item.Actions.All(action => Matches(registry.Read(action.Hive, action.Path, action.Name), action.Target))
                ? TweakStates.Applied
                : TweakStates.NotApplied;

        return new TweakStatus(
            item.Id,
            item.Title,
            item.Category,
            item.Group,
            item.Risk,
            item.Impact,
            item.Restart,
            item.Presets,
            item.RequiresAdmin,
            state,
            item.Description);
    }

    internal static bool Matches(RegistryValue? current, RegistryValue? target) =>
        target is null ? current is null : target.SameAs(current);

    private static string Show(RegistryValue? value) => value is null ? "Not set" : value.Data;
}

/// <summary>Makes and reverses changes. Every change is journaled before it happens.</summary>
public sealed class TweakEngine(
    TweakCatalog catalog,
    IRegistryStore registry,
    Journal journal,
    IRestorePointService restorePoints,
    string userSid,
    int osBuild,
    string appVersion)
{
    public ApplyResult Apply(ApplyRequest request)
    {
        var items = catalog.Resolve(request.Ids);
        var restorePoint = request.CreateRestorePoint ? restorePoints.Create("TuneForge changes") : "skipped";
        var changeSetId = journal.BeginChangeSet(userSid, osBuild, appVersion, restorePoint);

        var results = new List<TweakResult>();
        foreach (var item in items)
        {
            results.Add(item.Supports(osBuild)
                ? ApplyItem(changeSetId, item)
                : new TweakResult(item.Id, TweakStates.Unsupported, "This version of Windows doesn't support it."));
        }

        return new ApplyResult(changeSetId, restorePoint, results);
    }

    /// <summary>Applies one change built by a service, journaled so History can undo it.</summary>
    public OperationResult ApplyBuilt(TweakItem item)
    {
        var changeSetId = journal.BeginChangeSet(userSid, osBuild, appVersion, "skipped");
        var result = ApplyItem(changeSetId, item);
        return result.Status == TweakStates.Applied
            ? new OperationResult(true, "Saved. You can undo this from History.", item.Restart == "reboot")
            : new OperationResult(false, result.Message ?? "Windows refused this change.");
    }

    public UndoResult Undo(long changeSetId)
    {
        var owner = journal.GetSetUser(changeSetId) ?? throw new CatalogRequestException("That change no longer exists.");
        if (owner != userSid)
        {
            throw new CatalogRequestException("Another Windows user made that change.");
        }

        var restored = 0;
        var problems = new List<string>();
        foreach (var entry in journal.GetUndoable(changeSetId))
        {
            try
            {
                var current = registry.Read(entry.Hive, entry.Path, entry.Name);
                var untouched = TweakScanner.Matches(current, entry.OldValue);
                var stillOurs = TweakScanner.Matches(current, entry.NewValue);

                // A pending entry may never have been written, in which case it holds the old value.
                if (!untouched && !stillOurs)
                {
                    problems.Add($"{entry.TweakId}: this setting was changed by something else, so it was left alone.");
                    continue;
                }

                if (!untouched)
                {
                    Restore(entry);
                    restored++;
                }

                journal.MarkItemUndone(entry.Id);
            }
            catch (Exception exception) when (IsRegistryFailure(exception))
            {
                problems.Add($"{entry.TweakId}: Windows refused the change.");
            }
        }

        if (problems.Count == 0)
        {
            journal.MarkSetUndone(changeSetId);
        }

        return new UndoResult(changeSetId, restored, problems);
    }

    private TweakResult ApplyItem(long changeSetId, TweakItem item)
    {
        var done = new List<JournalEntry>();
        try
        {
            foreach (var action in item.Actions)
            {
                var target = action.Target;
                var before = registry.Read(action.Hive, action.Path, action.Name);
                if (TweakScanner.Matches(before, target))
                {
                    continue;
                }

                var keyExisted = registry.KeyExists(action.Hive, action.Path);
                var id = journal.RecordPending(
                    changeSetId, item.Id, action.Hive, action.Path, action.Name, keyExisted, before, target);
                done.Add(new JournalEntry(id, item.Id, action.Hive, action.Path, action.Name, keyExisted, before, target, "pending"));

                if (target is null)
                {
                    registry.DeleteValue(action.Hive, action.Path, action.Name);
                }
                else
                {
                    registry.Write(action.Hive, action.Path, action.Name, target);
                }

                if (!TweakScanner.Matches(registry.Read(action.Hive, action.Path, action.Name), target))
                {
                    throw new IOException("The setting didn't keep its new value.");
                }
            }

            foreach (var entry in done)
            {
                journal.SetStatus(entry.Id, "applied");
            }

            return new TweakResult(item.Id, TweakStates.Applied, null);
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            return RollBack(item, done);
        }
    }

    /// <summary>A tweak applies fully or not at all, so a partial failure is reversed at once.</summary>
    private TweakResult RollBack(TweakItem item, List<JournalEntry> done)
    {
        var clean = true;
        foreach (var entry in Enumerable.Reverse(done))
        {
            try
            {
                Restore(entry);
                journal.SetStatus(entry.Id, "reverted");
            }
            catch (Exception exception) when (IsRegistryFailure(exception))
            {
                clean = false;
                journal.SetStatus(entry.Id, "pending", exception.Message);
            }
        }

        var message = clean
            ? "Windows refused this change, so nothing was changed."
            : "Windows refused this change and part of it couldn't be reversed. Use History to try again.";
        return new TweakResult(item.Id, "failed", message);
    }

    private void Restore(JournalEntry entry)
    {
        if (entry.OldValue is null)
        {
            registry.DeleteValue(entry.Hive, entry.Path, entry.Name);
        }
        else
        {
            registry.Write(entry.Hive, entry.Path, entry.Name, entry.OldValue);
        }

        if (!entry.KeyExisted)
        {
            registry.DeleteKeyIfEmpty(entry.Hive, entry.Path);
        }
    }

    private static bool IsRegistryFailure(Exception exception) =>
        exception is UnauthorizedAccessException or IOException or System.Security.SecurityException;
}
