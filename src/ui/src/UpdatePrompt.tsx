import { useCallback, useEffect, useState } from "react";
import { bridge, type UpdatePrompt } from "./bridge";
import { Logo } from "./Logo";

const POLL_MS = 5000;

const keyOf = (prompt: UpdatePrompt) => `${prompt.status.latest}:${prompt.nudge}`;

/** Asks the app, which does the checking, so nothing here touches the network. Later lasts until an important update comes back. */
export function useUpdatePrompt() {
  const [prompt, setPrompt] = useState<UpdatePrompt | null>(null);
  const [dismissed, setDismissed] = useState<string | null>(null);

  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;

    async function read() {
      try {
        const next = await bridge.updatePending();
        if (!stopped) {
          setPrompt(next);
        }
      } catch {
        // The next read tries again.
      }
      if (!stopped) {
        timer = setTimeout(read, POLL_MS);
      }
    }

    void read();
    return () => {
      stopped = true;
      clearTimeout(timer);
    };
  }, []);

  const later = useCallback(() => {
    if (prompt) {
      setDismissed(keyOf(prompt));
    }
  }, [prompt]);

  return { prompt: prompt && keyOf(prompt) !== dismissed ? prompt : null, later };
}

export function UpdatePromptPage({ prompt, onLater }: { prompt: UpdatePrompt; onLater: () => void }) {
  const { status } = prompt;
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);

  async function install() {
    setBusy(true);
    setProblem(null);
    try {
      const result = await bridge.updateInstall();
      if (!result.ok) {
        setProblem(result.message);
      }
    } catch (reason) {
      setProblem((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby="update-prompt-title" className="flex min-h-full flex-col items-center justify-center text-center">
      <Logo className="size-20" />
      <h1 id="update-prompt-title" className="mt-4 text-[27px] leading-tight font-semibold tracking-tight">
        {status.important ? "Important update" : "Update available"}
      </h1>
      <p className="mt-1 text-muted-foreground">
        TuneForge {status.latest} is ready. You have {status.current}.
      </p>
      {status.notes && <p className="mt-4 w-full max-w-md rounded-lg border border-border bg-card p-3 text-left whitespace-pre-line">{status.notes}</p>}
      <div role="status">{problem && <p className="mt-3 font-medium text-destructive">{problem}</p>}</div>
      <div className="mt-4 flex gap-2">
        <button type="button" disabled={busy} onClick={onLater} className="min-h-11 cursor-pointer rounded-md border border-border px-4 font-medium disabled:cursor-not-allowed disabled:opacity-60">
          Later
        </button>
        <button
          type="button"
          disabled={busy}
          aria-busy={busy}
          onClick={install}
          className="min-h-11 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary disabled:cursor-wait disabled:opacity-60"
        >
          {busy ? "Downloading" : "Update now"}
        </button>
      </div>
    </section>
  );
}
