import { LoaderCircle } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { bridge, type ChangeSet, type UndoResult } from "./bridge";

export function HistoryPage() {
  const [sets, setSets] = useState<ChangeSet[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [undoing, setUndoing] = useState<number | null>(null);
  const [outcome, setOutcome] = useState<UndoResult | null>(null);
  const [confirmingAll, setConfirmingAll] = useState(false);
  const ALL = -1;

  async function undoAll() {
    setConfirmingAll(false);
    setUndoing(ALL);
    setError(null);
    setOutcome(null);
    try {
      setOutcome(await bridge.undoAll());
      load();
    } catch (reason) {
      setError((reason as Error).message);
    } finally {
      setUndoing(null);
    }
  }

  const active = sets?.filter((set) => !set.undoneUtc && set.tweakIds.length > 0).length ?? 0;

  const load = useCallback(() => {
    bridge.history().then(setSets, (reason: Error) => setError(reason.message));
  }, []);

  useEffect(load, [load]);

  async function undo(id: number) {
    setUndoing(id);
    setError(null);
    setOutcome(null);
    try {
      setOutcome(await bridge.undo(id));
      load();
    } catch (reason) {
      setError((reason as Error).message);
    } finally {
      setUndoing(null);
    }
  }

  return (
    <div className="flex flex-col gap-3">
      {active > 0 && (
        <div className="rounded-md border border-border bg-card p-4">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="min-w-0">
              <p className="font-medium">Put everything back</p>
              <p className="text-sm text-muted-foreground">Undo every change TuneForge has made, newest first.</p>
            </div>
            <button
              type="button"
              disabled={undoing !== null}
              aria-busy={undoing === ALL}
              onClick={() => setConfirmingAll(true)}
              className="inline-flex min-h-11 cursor-pointer items-center gap-2 rounded-md border border-border px-4 font-medium disabled:cursor-wait disabled:opacity-60"
            >
              {undoing === ALL && <LoaderCircle aria-hidden="true" className="size-5 animate-spin" />}
              {undoing === ALL ? "Undoing" : "Undo everything"}
            </button>
          </div>
          {confirmingAll && (
            <div className="mt-3 flex flex-wrap items-center gap-2">
              <span className="font-medium">
                {active === 1 ? "Undo your 1 change?" : `Undo all ${active} changes?`} This also puts back any locks you removed. Windows asks for permission once.
              </span>
              <button type="button" onClick={undoAll} className="min-h-11 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary">
                Yes, undo everything
              </button>
              <button type="button" onClick={() => setConfirmingAll(false)} className="min-h-11 cursor-pointer rounded-md border border-border px-4 font-medium">
                Cancel
              </button>
            </div>
          )}
        </div>
      )}
      {error && (
        <p role="alert" className="font-medium text-destructive">
          {error}
        </p>
      )}
      <div role="status">
        {outcome && (
          <div className="rounded-md border border-border bg-card p-3">
            <p className="font-medium text-success">Restored {outcome.restored} settings.</p>
            {outcome.problems.map((problem) => (
              <p key={problem} className="text-sm text-muted-foreground">
                {problem}
              </p>
            ))}
          </div>
        )}
      </div>

      {sets === null && !error && <p className="text-muted-foreground">Reading your history.</p>}
      {sets?.length === 0 && <p className="text-muted-foreground">No changes have been made yet.</p>}

      {sets?.map((set) => (
        <article key={set.id} className="rounded-md border border-border bg-card p-4">
          <div className="flex items-start justify-between gap-4">
            <div>
              <h2 className="font-semibold">{new Date(set.createdUtc).toLocaleString()}</h2>
              <p className="text-muted-foreground">
                {set.tweakIds.length} {set.tweakIds.length === 1 ? "tweak" : "tweaks"}
                {set.undoneUtc ? ", undone" : ""}
              </p>
            </div>
            {!set.undoneUtc && set.tweakIds.length > 0 && (
              <button
                type="button"
                disabled={undoing !== null}
                aria-busy={undoing === set.id}
                onClick={() => undo(set.id)}
                className="inline-flex min-h-11 cursor-pointer items-center gap-2 rounded-md border border-border px-4 font-medium disabled:cursor-wait disabled:opacity-60"
              >
                {undoing === set.id && <LoaderCircle aria-hidden="true" className="size-5 animate-spin" />}
                {undoing === set.id ? "Undoing" : "Undo"}
              </button>
            )}
          </div>
        </article>
      ))}
    </div>
  );
}
