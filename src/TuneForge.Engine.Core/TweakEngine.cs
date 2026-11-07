using TuneForge.Contracts;
using TuneForge.Engine.Core.Catalog;

namespace TuneForge.Engine.Core;

/// <summary>Reads tweak state without changing anything. Safe to run without admin rights.</summary>
public sealed class TweakScanner(TweakCatalog catalog, IRegistryStore registry, int osBuild)
{
    /// <summary>
    /// ours is what TuneForge wrote itself, which History can already undo. Without it nothing is called locked,
    /// because a setting TuneForge made itself would otherwise be offered for removal.
    /// </summary>
    public IReadOnlyList<TweakStatus> Scan(IReadOnlySet<string>? ours = null) => catalog.Items.Select(item => ToStatus(item, ours)).ToList();

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
                .ToList(),
            item.Hazard))
        .ToList();

    private TweakStatus ToStatus(TweakItem item, IReadOnlySet<string>? ours)
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
            item.Description,
            item.Hazard,
            ours is not null && item.Supports(osBuild) && item.Actions.Any(action => IsLocking(action) && !ours.Contains(action.Display)));
    }

    private bool IsLocking(RegistryAction action) =>
        action is { IsPolicy: true, Delete: false } && Matches(registry.Read(action.Hive, action.Path, action.Name), action.Target);

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

        // Checked here too, so no preset, file or screen can reach these without an explicit yes.
        if (!request.AcceptHazards && items.FirstOrDefault(item => item.Hazard) is { } hazard)
        {
            throw new CatalogRequestException($"{hazard.Title} needs confirming, because it's extremely hazardous for Riot Vanguard.");
        }

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

    /// <summary>
    /// Deletes the policy values this tweak would set, whoever set them, so Windows uses its own default again.
    /// It's journaled like any other change, so History can put the lock back. Ordinary settings are left alone,
    /// because the catalog only knows what a tweak sets, not what each PC had before.
    /// </summary>
    public OperationResult RemoveLock(string id) => RemoveLocks([id]);

    /// <summary>Several at once share one change, so one Windows prompt and one Undo in History.</summary>
    public OperationResult RemoveLocks(IReadOnlyList<string> ids)
    {
        // One at a time, because Resolve refuses two choices from the same group, and both may be locked.
        var built = ids.Distinct(StringComparer.Ordinal)
            .Select(id => catalog.Resolve([id])[0])
            .Select(item => item with
            {
                Id = $"remove-lock:{item.Id}",
                Actions = item.Actions
                    .Where(action => action is { IsPolicy: true, Delete: false } && TweakScanner.Matches(registry.Read(action.Hive, action.Path, action.Name), action.Target))
                    .Select(action => action with { Kind = null, Value = null, Delete = true })
                    .ToList(),
            })
            .Where(item => item.Actions.Count > 0)
            .ToList();

        if (built.Count == 0)
        {
            throw new CatalogRequestException(ids.Count == 1 ? "That setting isn't locked." : "None of these settings are locked.");
        }

        var changeSetId = journal.BeginChangeSet(userSid, osBuild, appVersion, "skipped");
        var failed = built.Select(item => ApplyItem(changeSetId, item)).Count(result => result.Status != TweakStates.Applied);
        var removed = built.Count - failed;
        if (removed == 0)
        {
            return new OperationResult(false, "Windows refused this change, so nothing was changed.");
        }

        var message = built.Count == 1 ? "Saved. You can undo this from History."
            : failed == 0 ? $"Removed {removed} locks. You can undo them together from History."
            : $"Removed {removed} of {built.Count} locks. Windows refused the rest. You can undo this from History.";
        return new OperationResult(failed == 0, message, built.Any(item => item.Restart == "reboot"));
    }

    /// <summary>
    /// Undoes every active change this user made, newest first, so later changes come off before earlier ones.
    /// Anything another tool changed since is left alone and reported, as a single undo would.
    /// </summary>
    public UndoResult UndoAll()
    {
        var active = journal.ListChangeSets(int.MaxValue).Where(set => set.UndoneUtc is null).ToList();
        if (active.Count == 0)
        {
            throw new CatalogRequestException("There's nothing to undo.");
        }

        var restored = 0;
        var problems = new List<string>();
        foreach (var set in active)
        {
            if (journal.GetSetUser(set.Id) != userSid)
            {
                problems.Add($"Change {set.Id} was made by another Windows user, so it was left alone.");
                continue;
            }

            var result = Undo(set.Id);
            restored += result.Restored;
            problems.AddRange(result.Problems);
        }

        return new UndoResult(0, restored, problems);
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
