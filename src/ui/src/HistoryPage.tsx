import { LoaderCircle } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { bridge, type ChangeSet, type UndoResult } from "./bridge";

export function HistoryPage() {
  const [sets, setSets] = useState<ChangeSet[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [undoing, setUndoing] = useState<number | null>(null);
  const [outcome, setOutcome] = useState<UndoResult | null>(null);

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
        <article key={set.Id} className="rounded-md border border-border bg-card p-4">
          <div className="flex items-start justify-between gap-4">
            <div>
              <h2 className="font-semibold">{new Date(set.CreatedUtc).toLocaleString()}</h2>
              <p className="text-muted-foreground">
                {set.TweakIds.length} {set.TweakIds.length === 1 ? "tweak" : "tweaks"}
                {set.UndoneUtc ? ", undone" : ""}
              </p>
            </div>
            {!set.UndoneUtc && set.TweakIds.length > 0 && (
              <button
                type="button"
                disabled={undoing !== null}
                aria-busy={undoing === set.Id}
                onClick={() => undo(set.Id)}
                className="inline-flex min-h-11 cursor-pointer items-center gap-2 rounded-md border border-border px-4 font-medium disabled:cursor-wait disabled:opacity-60"
              >
                {undoing === set.Id && <LoaderCircle aria-hidden="true" className="size-5 animate-spin" />}
                {undoing === set.Id ? "Undoing" : "Undo"}
              </button>
            )}
          </div>
        </article>
      ))}
    </div>
  );
}
