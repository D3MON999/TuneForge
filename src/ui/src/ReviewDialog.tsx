import { LoaderCircle } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { type ApplyResult, bridge, type TweakPreview } from "./bridge";
import { restartText, strongestRestart } from "./selection";

interface ReviewDialogProps {
  ids: string[];
  onClose: (changed: boolean) => void;
}

type Stage =
  | { kind: "loading" }
  | { kind: "review"; previews: TweakPreview[] }
  | { kind: "applying"; previews: TweakPreview[] }
  | { kind: "done"; result: ApplyResult; previews: TweakPreview[] }
  | { kind: "failed"; message: string; previews: TweakPreview[] };

const restorePointText: Record<ApplyResult["restorePoint"], string> = {
  requested: "Restore point requested. Windows makes at most one a day, so it may reuse a recent one.",
  unavailable: "System Restore is off, so no restore point was made. You can still undo from History.",
  skipped: "No restore point was made. You can still undo from History.",
};

export function ReviewDialog({ ids, onClose }: ReviewDialogProps) {
  const dialog = useRef<HTMLDialogElement>(null);
  const [stage, setStage] = useState<Stage>({ kind: "loading" });
  const [restorePoint, setRestorePoint] = useState(true);

  useEffect(() => {
    dialog.current?.showModal();
    bridge.preview(ids).then(
      (previews) => setStage({ kind: "review", previews }),
      (error: Error) => setStage({ kind: "failed", message: error.message, previews: [] }),
    );
  }, [ids]);

  async function apply(previews: TweakPreview[]) {
    setStage({ kind: "applying", previews });
    try {
      setStage({ kind: "done", result: await bridge.apply(ids, restorePoint), previews });
    } catch (error) {
      setStage({ kind: "failed", message: (error as Error).message, previews });
    }
  }

  const busy = stage.kind === "applying";
  const changed = stage.kind === "done" && stage.result.results.some((item) => item.status === "applied");

  return (
    <dialog
      ref={dialog}
      aria-labelledby="review-title"
      onCancel={(event) => {
        event.preventDefault();
        if (!busy) {
          onClose(changed);
        }
      }}
      className="m-auto max-h-[85vh] w-[42rem] max-w-[90vw] rounded-lg border border-border bg-card p-6 text-foreground backdrop:bg-black/50"
    >
      <h2 id="review-title" className="text-xl font-semibold">
        {stage.kind === "done" ? "Changes finished" : `Review ${ids.length} ${ids.length === 1 ? "change" : "changes"}`}
      </h2>

      {stage.kind === "loading" && <p className="mt-4 text-muted-foreground">Reading your current settings.</p>}

      {(stage.kind === "review" || stage.kind === "applying") && (
        <>
          <ul className="mt-4 flex flex-col gap-2">
            {stage.previews.map((preview) => (
              <li key={preview.id} className="rounded-md border border-border p-3">
                <details>
                  <summary className="cursor-pointer font-medium">{preview.title}</summary>
                  <ul className="mt-2 flex flex-col gap-2 text-sm">
                    {preview.changes.map((change) => (
                      <li key={change.setting}>
                        <p className="break-all text-muted-foreground">{change.setting}</p>
                        <p>
                          {change.before} to {change.after}
                        </p>
                      </li>
                    ))}
                  </ul>
                </details>
                {preview.impact && <p className="mt-1 text-sm text-muted-foreground">{preview.impact}</p>}
              </li>
            ))}
          </ul>

          <p className="mt-4 text-muted-foreground">
            {restartText[strongestRestart(stage.previews.map((preview) => preview.restart))]}
          </p>
          <label className="mt-3 flex min-h-11 cursor-pointer items-center gap-3">
            <input
              type="checkbox"
              checked={restorePoint}
              disabled={busy}
              onChange={(event) => setRestorePoint(event.target.checked)}
              className="size-5"
            />
            Create a restore point first
          </label>
        </>
      )}

      {stage.kind === "failed" && (
        <p role="alert" className="mt-4 font-medium text-destructive">
          {stage.message}
        </p>
      )}

      {stage.kind === "done" && <ResultSummary result={stage.result} previews={stage.previews} />}

      <div className="mt-6 flex justify-end gap-3">
        {stage.kind === "review" || stage.kind === "applying" ? (
          <>
            <button
              type="button"
              disabled={busy}
              onClick={() => onClose(false)}
              className="min-h-11 cursor-pointer rounded-md border border-border px-4 font-medium disabled:cursor-not-allowed disabled:opacity-60"
            >
              Cancel
            </button>
            <button
              type="button"
              disabled={busy}
              aria-busy={busy}
              onClick={() => apply(stage.previews)}
              className="inline-flex min-h-11 cursor-pointer items-center gap-2 rounded-md bg-primary px-4 font-medium text-on-primary disabled:cursor-wait disabled:opacity-60"
            >
              {busy && <LoaderCircle aria-hidden="true" className="size-5 animate-spin" />}
              {busy ? "Applying" : "Apply changes"}
            </button>
          </>
        ) : (
          <button
            type="button"
            onClick={() => onClose(changed)}
            className="min-h-11 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary"
          >
            Close
          </button>
        )}
      </div>
    </dialog>
  );
}

function ResultSummary({ result, previews }: { result: ApplyResult; previews: TweakPreview[] }) {
  const title = (id: string) => previews.find((preview) => preview.id === id)?.title ?? id;
  const applied = result.results.filter((item) => item.status === "applied");
  const others = result.results.filter((item) => item.status !== "applied");

  return (
    <div role="status" className="mt-4 flex flex-col gap-3">
      <p className="font-medium text-success">
        {applied.length} of {result.results.length} applied.
      </p>
      {others.length > 0 && (
        <ul className="flex flex-col gap-2">
          {others.map((item) => (
            <li key={item.id}>
              <p className="font-medium text-destructive">{title(item.id)}</p>
              <p className="text-sm text-muted-foreground">{item.message}</p>
            </li>
          ))}
        </ul>
      )}
      <p className="text-muted-foreground">{restorePointText[result.restorePoint]}</p>
      {applied.length > 0 && (
        <p className="text-muted-foreground">
          {restartText[strongestRestart(previews.filter((preview) => applied.some((item) => item.id === preview.id)).map((preview) => preview.restart))]}
        </p>
      )}
    </div>
  );
}
