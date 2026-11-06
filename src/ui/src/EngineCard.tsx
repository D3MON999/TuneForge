import { LoaderCircle, ShieldCheck } from "lucide-react";
import { useState } from "react";
import { bridge, type PingResult } from "./bridge";

type EngineState =
  | { kind: "idle" }
  | { kind: "connecting" }
  | { kind: "connected"; ping: PingResult }
  | { kind: "failed"; message: string };

export function EngineCard() {
  const [state, setState] = useState<EngineState>({ kind: "idle" });

  async function connect() {
    setState({ kind: "connecting" });
    try {
      setState({ kind: "connected", ping: await bridge.connectEngine() });
    } catch (error) {
      setState({ kind: "failed", message: error instanceof Error ? error.message : "The request failed." });
    }
  }

  const busy = state.kind === "connecting";

  return (
    <section aria-labelledby="engine-title" className="rounded-lg border border-border bg-card p-6">
      <h2 id="engine-title" className="text-lg font-semibold">
        System engine
      </h2>
      <p className="mt-1 text-muted-foreground">
        TuneForge asks Windows for admin permission only when the engine starts.
      </p>

      <button
        type="button"
        onClick={connect}
        disabled={busy}
        aria-busy={busy}
        className="mt-4 inline-flex min-h-11 cursor-pointer items-center gap-2 rounded-md bg-primary px-4 font-medium text-on-primary transition-opacity duration-200 hover:opacity-90 disabled:cursor-wait disabled:opacity-60"
      >
        {busy ? (
          <LoaderCircle aria-hidden="true" className="size-5 animate-spin" />
        ) : (
          <ShieldCheck aria-hidden="true" className="size-5" />
        )}
        {busy ? "Connecting" : "Connect engine"}
      </button>

      <div role="status" className="mt-4 min-h-6">
        {state.kind === "connected" && (
          <dl className="grid max-w-sm grid-cols-[auto_1fr] gap-x-6 gap-y-1">
            <dt className="text-muted-foreground">Status</dt>
            <dd className="font-medium text-success">Connected</dd>
            <dt className="text-muted-foreground">Admin rights</dt>
            <dd>{state.ping.elevated ? "Yes" : "No"}</dd>
            <dt className="text-muted-foreground">Engine version</dt>
            <dd>{state.ping.engineVersion}</dd>
          </dl>
        )}
        {state.kind === "failed" && <p className="font-medium text-destructive">{state.message}</p>}
      </div>
    </section>
  );
}
