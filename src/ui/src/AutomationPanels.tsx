import { useEffect, useState } from "react";
import { bridge, type AppSettings, type MemoryResult, type SettingsChoices } from "./bridge";
import { formatBytes } from "./format";

type BooleanKey = {
  [Key in keyof SettingsChoices]: SettingsChoices[Key] extends boolean ? Key : never;
}[keyof SettingsChoices];

function Toggle({
  label,
  note,
  checked,
  onChange,
}: {
  label: string;
  note: string;
  checked: boolean;
  onChange: (value: boolean) => void;
}) {
  return (
    <label className="flex min-h-11 cursor-pointer items-start gap-3">
      <input
        type="checkbox"
        checked={checked}
        onChange={(event) => onChange(event.target.checked)}
        className="mt-1 size-5 shrink-0"
      />
      <span>
        <span className="block font-medium">{label}</span>
        <span className="block text-sm text-muted-foreground">{note}</span>
      </span>
    </label>
  );
}

function NumberField({
  label,
  value,
  min,
  max,
  onCommit,
}: {
  label: string;
  value: number;
  min: number;
  max: number;
  onCommit: (value: number) => void;
}) {
  const [text, setText] = useState(String(value));
  useEffect(() => setText(String(value)), [value]);

  return (
    <label className="ml-8 flex items-center gap-3">
      <span className="text-sm text-muted-foreground">{label}</span>
      <input
        type="number"
        min={min}
        max={max}
        value={text}
        onChange={(event) => setText(event.target.value)}
        onBlur={() => {
          const parsed = Number(text);
          if (Number.isFinite(parsed) && text !== "" && parsed !== value) {
            onCommit(parsed);
          } else {
            setText(String(value));
          }
        }}
        className="min-h-11 w-20 rounded-md border border-border bg-background px-3"
      />
    </label>
  );
}

export function AutomationSettings() {
  const [settings, setSettings] = useState<AppSettings | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    bridge.settingsGet().then(setSettings, (reason: Error) => setError(reason.message));
  }, []);

  async function save(changes: Partial<SettingsChoices>) {
    if (!settings) {
      return;
    }

    setError(null);
    try {
      setSettings(await bridge.settingsSave({ ...settings.choices, ...changes }));
    } catch (reason) {
      setError((reason as Error).message);
    }
  }

  if (!settings) {
    return error ? <p role="alert" className="font-medium text-destructive">{error}</p> : null;
  }

  const { choices } = settings;
  const toggle = (key: BooleanKey) => (value: boolean) => save({ [key]: value });
  const running = choices.closeToTray || choices.startWithWindows;
  const automatic = choices.memoryAuto || choices.cleanAuto || choices.startupWatch;

  return (
    <>
      <section aria-labelledby="background-title" className="rounded-lg border border-border bg-card p-6">
        <h2 id="background-title" className="text-lg font-semibold">
          Tray
        </h2>
        {error && <p role="alert" className="mt-2 font-medium text-destructive">{error}</p>}
        <div className="mt-3 flex flex-col gap-3">
          <Toggle
            label="Keep running in the tray when closed"
            note="Closing the window hides TuneForge. Right-click the tray icon to exit."
            checked={choices.closeToTray}
            onChange={toggle("closeToTray")}
          />
          <Toggle
            label="Start with Windows"
            note="Starts hidden in the tray when you sign in."
            checked={choices.startWithWindows}
            onChange={toggle("startWithWindows")}
          />
          <Toggle
            label="Show notifications"
            note="Tells you when an automation has run."
            checked={choices.notifications}
            onChange={toggle("notifications")}
          />
        </div>
      </section>

      <section aria-labelledby="automation-title" className="rounded-lg border border-border bg-card p-6">
        <h2 id="automation-title" className="text-lg font-semibold">
          Automations
        </h2>
        <p className="mt-1 text-muted-foreground">
          These run only while TuneForge is open or in the tray, and never ask for admin rights.
        </p>
        {automatic && !running && (
          <p role="status" className="mt-2 font-medium text-warning">
            They stop when you close TuneForge. Turn on a tray option above to keep them running.
          </p>
        )}
        <div className="mt-3 flex flex-col gap-3">
          <Toggle
            label="Free memory when it runs low"
            note="Runs at most every 30 minutes and leaves the active window alone. It helps only when memory is tight and won't raise FPS."
            checked={choices.memoryAuto}
            onChange={toggle("memoryAuto")}
          />
          {choices.memoryAuto && (
            <NumberField
              label="Run at this percent of memory used"
              value={choices.memoryThreshold}
              min={50}
              max={95}
              onCommit={(memoryThreshold) => save({ memoryThreshold })}
            />
          )}
          <Toggle
            label="Clean junk files on a schedule"
            note="Deletes the safe categories that need no admin rights. The first clean waits one full interval."
            checked={choices.cleanAuto}
            onChange={toggle("cleanAuto")}
          />
          {choices.cleanAuto && (
            <NumberField
              label="Days between cleans"
              value={choices.cleanDays}
              min={1}
              max={90}
              onCommit={(cleanDays) => save({ cleanDays })}
            />
          )}
          <Toggle
            label="Alert me about new startup programs"
            note="Tells you when something adds itself to Windows startup."
            checked={choices.startupWatch}
            onChange={toggle("startupWatch")}
          />
        </div>
      </section>
    </>
  );
}

export function MemoryCard() {
  const [running, setRunning] = useState(false);
  const [result, setResult] = useState<MemoryResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function run() {
    setRunning(true);
    setError(null);
    setResult(null);
    try {
      setResult(await bridge.memoryClean());
    } catch (reason) {
      setError((reason as Error).message);
    } finally {
      setRunning(false);
    }
  }

  return (
    <section aria-labelledby="memory-title" className="mt-6 rounded-lg border border-border bg-card p-6">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0 flex-1">
          <h2 id="memory-title" className="text-lg font-semibold">
            Free memory
          </h2>
          <p className="text-muted-foreground">
            Asks programs to give back memory they aren't using. It helps only when memory is tight and won't raise FPS.
          </p>
        </div>
        <button
          type="button"
          disabled={running}
          aria-busy={running}
          onClick={run}
          className="min-h-11 shrink-0 cursor-pointer rounded-md border border-border px-4 font-medium disabled:cursor-wait disabled:opacity-60"
        >
          {running ? "Working" : "Free memory"}
        </button>
      </div>
      <div role="status">
        {result && (
          <p className="mt-2 font-medium text-success">
            Freed {formatBytes(result.freedBytes)}. Memory in use went from {formatBytes(result.usedBeforeBytes)} to{" "}
            {formatBytes(result.usedAfterBytes)}.
          </p>
        )}
        {error && <p className="mt-2 font-medium text-destructive">{error}</p>}
      </div>
    </section>
  );
}
