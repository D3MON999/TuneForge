import { useState } from "react";
import { bridge, type OperationResult, type UpdateStatus } from "./bridge";

const buttonClass =
  "min-h-11 shrink-0 cursor-pointer rounded-md border border-border px-4 font-medium disabled:cursor-wait disabled:opacity-60";

export function UpdateCard() {
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<UpdateStatus | null>(null);
  const [result, setResult] = useState<OperationResult | string | null>(null);

  async function check() {
    setBusy(true);
    setResult(null);
    try {
      setStatus(await bridge.updateCheck());
    } catch (reason) {
      setResult((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function install() {
    setBusy(true);
    setResult(null);
    try {
      setResult(await bridge.updateInstall());
    } catch (reason) {
      setResult((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  const failed = typeof result === "string" || result?.ok === false;
  const text = typeof result === "string" ? result : result?.message;
  return (
    <section aria-labelledby="update-title" className="rounded-lg border border-border bg-card p-6">
      <h2 id="update-title" className="text-lg font-semibold">
        Updates
      </h2>
      <p className="mt-1 text-muted-foreground">TuneForge checks for updates by itself and asks before installing. It only installs updates signed by the developer.</p>
      <div className="mt-3 flex flex-wrap gap-2">
        <button type="button" disabled={busy} aria-busy={busy} onClick={check} className={buttonClass}>
          {busy ? "Working" : "Check for updates"}
        </button>
        {status?.available && (
          <button
            type="button"
            disabled={busy}
            onClick={install}
            className="min-h-11 shrink-0 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary disabled:cursor-wait disabled:opacity-60"
          >
            Install version {status.latest}
          </button>
        )}
      </div>
      <div role="status">
        {status && !text && <p className="mt-2">{status.message}</p>}
        {text && <p className={`mt-2 font-medium ${failed ? "text-destructive" : "text-success"}`}>{text}</p>}
      </div>
    </section>
  );
}
