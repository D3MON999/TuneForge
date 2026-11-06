import { ShieldAlert } from "lucide-react";
import { useEffect, useRef, useState } from "react";

export const hazardLabel = "Extremely hazardous for Riot Vanguard";

/** Explains the risk without promising more than anyone knows, in either direction. */
export function HazardNotice() {
  return (
    <div className="rounded-md border border-destructive p-3">
      <p className="flex items-center gap-2 font-semibold text-destructive">
        <ShieldAlert aria-hidden="true" className="size-5 shrink-0" />
        {hazardLabel}
      </p>
      <p className="mt-1 text-sm">
        This changes how Windows starts, keeps time or trusts firmware. That's the same deep level where Vanguard runs and checks your PC.
      </p>
      <ul className="mt-2 ml-4 flex list-disc flex-col gap-0.5 text-sm">
        <li>VALORANT or League of Legends may refuse to start.</li>
        <li>Riot doesn't list what's allowed, so nobody can promise this is safe, and your account could be at risk.</li>
        <li>Other games with kernel-level anti-cheat may react the same way.</li>
      </ul>
      <p className="mt-2 text-sm font-medium">If you play Riot games, leave this off.</p>
    </div>
  );
}

export function HazardConfirmTick({ checked, onChange }: { checked: boolean; onChange: (value: boolean) => void }) {
  return (
    <label className="mt-3 flex min-h-11 cursor-pointer items-center gap-3">
      <input type="checkbox" checked={checked} onChange={(event) => onChange(event.target.checked)} />
      <span className="font-medium">I understand the risk to my Riot account</span>
    </label>
  );
}

/** The confirmation window. Keeping it off is the easy choice, and turning it on takes two steps. */
export function HazardDialog({
  title,
  description,
  onConfirm,
  onCancel,
}: {
  title: string;
  description: string;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const [understood, setUnderstood] = useState(false);

  useEffect(() => {
    dialog.current?.showModal();
  }, []);

  return (
    <dialog
      ref={dialog}
      aria-labelledby="hazard-title"
      onCancel={(event) => {
        event.preventDefault();
        onCancel();
      }}
      className="m-auto max-h-[85vh] w-[34rem] max-w-[90vw] rounded-lg border border-border bg-card p-6 text-foreground backdrop:bg-black/70"
    >
      <h2 id="hazard-title" className="text-xl font-semibold">
        Turn on {title}?
      </h2>
      <p className="mt-1 mb-3 text-muted-foreground">{description}</p>
      <HazardNotice />
      <HazardConfirmTick checked={understood} onChange={setUnderstood} />
      <div className="mt-5 flex justify-end gap-3">
        <button type="button" onClick={onCancel} className="min-h-11 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary">
          Keep it off
        </button>
        <button
          type="button"
          disabled={!understood}
          onClick={onConfirm}
          className="min-h-11 cursor-pointer rounded-md border border-destructive px-4 font-medium text-destructive disabled:cursor-not-allowed disabled:opacity-50"
        >
          Turn on anyway
        </button>
      </div>
    </dialog>
  );
}
