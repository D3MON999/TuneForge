import { LoaderCircle } from "lucide-react";
import { useEffect, useState } from "react";
import { bridge, type OperationResult, type RepairInfo } from "./bridge";

export function RepairPage() {
  const [repairs, setRepairs] = useState<RepairInfo[] | null>(null);
  const [running, setRunning] = useState<string | null>(null);
  const [results, setResults] = useState<Record<string, OperationResult | string>>({});

  useEffect(() => {
    bridge.repairList().then(setRepairs, () => setRepairs([]));
  }, []);

  async function run(id: string) {
    setRunning(id);
    try {
      const result = await bridge.repairRun(id);
      setResults((current) => ({ ...current, [id]: result }));
    } catch (reason) {
      setResults((current) => ({ ...current, [id]: (reason as Error).message }));
    } finally {
      setRunning(null);
    }
  }

  return (
    <div className="flex flex-col gap-3">
      {repairs === null && <p className="text-muted-foreground">Loading repairs.</p>}
      {repairs?.map((repair) => {
        const result = results[repair.id];
        const failed = typeof result === "string" || result?.ok === false;
        return (
          <article key={repair.id} className="rounded-md border border-border bg-card p-4">
            <div className="flex items-start justify-between gap-4">
              <div>
                <h2 className="font-semibold">{repair.title}</h2>
                <p className="text-muted-foreground">{repair.note}</p>
                {repair.longRunning && <p className="text-sm text-muted-foreground">This can take 15 minutes or more.</p>}
              </div>
              <button
                type="button"
                disabled={running !== null}
                aria-busy={running === repair.id}
                aria-label={`Run ${repair.title}`}
                onClick={() => run(repair.id)}
                className="inline-flex min-h-11 shrink-0 cursor-pointer items-center gap-2 rounded-md border border-border px-4 font-medium disabled:cursor-wait disabled:opacity-60"
              >
                {running === repair.id && <LoaderCircle aria-hidden="true" className="size-5 animate-spin" />}
                {running === repair.id ? "Running" : "Run"}
              </button>
            </div>
            <div role="status">
              {result && (
                <p className={`mt-2 font-medium ${failed ? "text-destructive" : "text-success"}`}>
                  {typeof result === "string" ? result : result.message}
                </p>
              )}
            </div>
          </article>
        );
      })}
    </div>
  );
}
