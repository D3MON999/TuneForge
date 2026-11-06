import { LoaderCircle } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { bridge, type CleanupCategory, type CleanupResult } from "./bridge";
import { formatBytes } from "./format";

export function CleanupPage() {
  const [categories, setCategories] = useState<CleanupCategory[] | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [cleaning, setCleaning] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [result, setResult] = useState<CleanupResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  const scan = useCallback((resetSelection: boolean) => {
    setCategories(null);
    bridge.cleanupScan().then(
      (found) => {
        setCategories(found);
        if (resetSelection) {
          setSelected(new Set(found.filter((item) => item.defaultOn).map((item) => item.id)));
        }
      },
      (reason: Error) => setError(reason.message),
    );
  }, []);

  useEffect(() => scan(true), [scan]);

  function toggle(id: string) {
    const next = new Set(selected);
    if (!next.delete(id)) {
      next.add(id);
    }
    setSelected(next);
    setConfirming(false);
  }

  async function clean() {
    setConfirming(false);
    setCleaning(true);
    setError(null);
    setResult(null);
    try {
      setResult(await bridge.cleanupRun([...selected]));
      scan(false);
    } catch (reason) {
      setError((reason as Error).message);
    } finally {
      setCleaning(false);
    }
  }

  const picked = categories?.filter((item) => selected.has(item.id)) ?? [];
  const total = picked.reduce((sum, item) => sum + item.bytes, 0);

  return (
    <div className="flex flex-col gap-3">
      {error && (
        <p role="alert" className="font-medium text-destructive">
          {error}
        </p>
      )}
      <div role="status">
        {result && (
          <p className="rounded-md border border-border bg-card p-3 font-medium text-success">
            Freed {formatBytes(result.freedBytes)} from {result.files} files.
            {result.skipped > 0 && ` ${result.skipped} files were in use and were left alone.`}
          </p>
        )}
      </div>

      {!categories && !error && <p className="text-muted-foreground">Measuring files.</p>}

      {categories?.map((item) => (
        <label key={item.id} className="flex min-h-11 cursor-pointer items-start gap-3 rounded-md border border-border bg-card p-3">
          <input type="checkbox" checked={selected.has(item.id)} onChange={() => toggle(item.id)} />
          <span className="shrink-0 tabular-nums text-muted-foreground">{formatBytes(item.bytes)}</span>
          <span className="min-w-0 flex-1">
            <span className="font-medium">{item.title}</span>
            <span className="block text-sm text-muted-foreground">{item.note}</span>
          </span>
        </label>
      ))}

      {categories && (
        <div className="flex flex-wrap items-center gap-3 pt-2">
          {confirming ? (
            <>
              <p>
                Permanently delete {formatBytes(total)}? This can't be undone.
              </p>
              <button
                type="button"
                onClick={clean}
                className="min-h-11 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary"
              >
                Delete files
              </button>
              <button
                type="button"
                onClick={() => setConfirming(false)}
                className="min-h-11 cursor-pointer rounded-md border border-border px-4 font-medium"
              >
                Cancel
              </button>
            </>
          ) : (
            <button
              type="button"
              disabled={picked.length === 0 || cleaning}
              aria-busy={cleaning}
              onClick={() => setConfirming(true)}
              className="inline-flex min-h-11 cursor-pointer items-center gap-2 rounded-md bg-primary px-4 font-medium text-on-primary disabled:cursor-not-allowed disabled:opacity-60"
            >
              {cleaning && <LoaderCircle aria-hidden="true" className="size-5 animate-spin" />}
              {cleaning ? "Cleaning" : `Clean ${formatBytes(total)}`}
            </button>
          )}
        </div>
      )}
    </div>
  );
}
