import { CircleAlert, CircleCheck, CircleHelp } from "lucide-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { bridge, type AdvancedEntry, type OperationResult, type ProcessEntry, type SecurityStatus, type ToolLink } from "./bridge";
import { formatBytes } from "./format";
import { HazardDialog, hazardLabel } from "./Hazard";
import { matchesText } from "./selection";

const buttonClass =
  "min-h-11 shrink-0 cursor-pointer rounded-md border border-border px-4 font-medium disabled:cursor-not-allowed disabled:opacity-60";
const cardClass = "rounded-lg border border-border bg-card p-4";

function Outcome({ result }: { result: OperationResult | string | null }) {
  const text = typeof result === "string" ? result : result?.message;
  const failed = typeof result === "string" || result?.ok === false;
  return <div role="status">{text && <p className={`mb-3 font-medium ${failed ? "text-destructive" : "text-success"}`}>{text}</p>}</div>;
}

const riskText = { low: "", medium: "Medium risk", high: "High risk" };
const riskColor = { low: "", medium: "text-warning", high: "text-destructive" };

/** System switches that a tool changes. They need a warning before anything happens. */
export function AdvancedSwitches() {
  const [items, setItems] = useState<AdvancedEntry[] | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [pending, setPending] = useState<AdvancedEntry | null>(null);
  const [result, setResult] = useState<OperationResult | string | null>(null);

  const reload = useCallback(() => {
    bridge.advancedList().then(setItems, (reason: Error) => setResult(reason.message));
  }, []);
  useEffect(reload, [reload]);

  async function change(item: AdvancedEntry) {
    setPending(null);
    setBusy(item.id);
    try {
      const accepted = Boolean(item.hazard) && !item.enabled;
      setResult(await bridge.advancedSet(item.id, !item.enabled, accepted));
      reload();
    } catch (reason) {
      setResult((reason as Error).message);
    } finally {
      setBusy(null);
    }
  }

  return (
    <section aria-labelledby="switches-title" className="mt-6">
      <h2 id="switches-title" className="font-semibold text-muted-foreground">
        System switches
      </h2>
      <p className="mt-1 mb-2 text-sm text-warning">
        These run Windows tools directly, so they aren't in History. Use the same switch to turn them back off.
      </p>
      <Outcome result={result} />
      <div className="flex flex-col gap-1.5">
        {items?.map((item) => (
          <div key={item.id} className="rounded-lg border border-border bg-card px-4 py-2.5">
            <div className="flex items-center justify-between gap-3">
              <div className="min-w-0">
                <p className="flex flex-wrap items-center gap-2 font-medium">
                  {item.title}
                  {item.hazard ? (
                    <span className="text-sm font-semibold text-destructive">{hazardLabel}</span>
                  ) : (
                    item.risk !== "low" && <span className={`text-sm ${riskColor[item.risk]}`}>{riskText[item.risk]}</span>
                  )}
                  {item.enabled && <span className="text-sm text-success">On</span>}
                </p>
                <p className="text-sm text-muted-foreground">{item.description}</p>
                <p className="text-sm text-warning">{item.warning}</p>
              </div>
              <button
                type="button"
                disabled={busy !== null || (item.enabled && !item.reversible)}
                aria-busy={busy === item.id}
                aria-label={`${item.enabled ? "Turn off" : "Turn on"} ${item.title}`}
                onClick={() => setPending(item)}
                className={buttonClass}
              >
                {busy === item.id ? "Working" : item.enabled ? "Turn off" : item.reversible ? "Turn on" : "Remove"}
              </button>
            </div>
            {pending?.id === item.id && item.hazard && !item.enabled && (
              <HazardDialog title={item.title} description={item.description} onCancel={() => setPending(null)} onConfirm={() => change(item)} />
            )}
            {pending?.id === item.id && !(item.hazard && !item.enabled) && (
              <div className="mt-2 flex flex-wrap items-center gap-2">
                <span className="font-medium">{item.reversible ? "Are you sure?" : "This can't be undone here. Are you sure?"}</span>
                <button
                  type="button"
                  onClick={() => change(item)}
                  className="min-h-11 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary"
                >
                  Yes, continue
                </button>
                <button type="button" onClick={() => setPending(null)} className={buttonClass}>
                  Cancel
                </button>
              </div>
            )}
          </div>
        ))}
      </div>
    </section>
  );
}

function StateIcon({ on }: { on?: boolean | null }) {
  if (on === true) {
    return <CircleCheck aria-hidden="true" className="size-5 shrink-0 text-success" />;
  }

  return on === false ? (
    <CircleAlert aria-hidden="true" className="size-5 shrink-0 text-warning" />
  ) : (
    <CircleHelp aria-hidden="true" className="size-5 shrink-0 text-muted-foreground" />
  );
}

const stateLabel = (on?: boolean | null) => (on === true ? "On" : on === false ? "Off" : "Not reported");

export function SecurityCard() {
  const [status, setStatus] = useState<SecurityStatus | null>(null);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<OperationResult | string | null>(null);

  const reload = useCallback(() => {
    bridge.securityRead().then(setStatus, (reason: Error) => setResult(reason.message));
  }, []);
  useEffect(reload, [reload]);

  async function turnOn(id: string) {
    setBusy(true);
    try {
      setResult(await bridge.securitySet(id));
      reload();
    } catch (reason) {
      setResult((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  if (!status) {
    return null;
  }

  const rows: { label: string; on?: boolean | null; note: string; action?: string }[] = [
    { label: "Antivirus", on: status.antivirus, note: "Microsoft Defender. Another antivirus can take over from it." },
    { label: "Real-time protection", on: status.realTimeProtection, note: "Scans files as they're opened." },
    { label: "Firewall", on: status.firewall, note: "Blocks unwanted network connections.", action: "firewall" },
    { label: "Controlled folder access", on: status.controlledFolderAccess, note: "Stops untrusted apps changing your documents. Helps against ransomware.", action: "controlled-folders" },
    { label: "BitLocker", on: status.bitLocker, note: "Encrypts your Windows drive. Turn it on from Windows Settings, and save your recovery key." },
  ];

  return (
    <section aria-labelledby="security-title" className={`${cardClass} mb-5`}>
      <h2 id="security-title" className="mb-2 font-semibold">
        Security status
      </h2>
      <Outcome result={result} />
      <ul className="flex flex-col gap-2">
        {rows.map((row) => (
          <li key={row.label} className="flex items-center gap-3">
            <StateIcon on={row.on} />
            <div className="min-w-0 flex-1">
              <p className="font-medium">
                {row.label}: {stateLabel(row.on)}
              </p>
              <p className="text-sm text-muted-foreground">{row.note}</p>
            </div>
            {row.action && row.on === false && (
              <button
                type="button"
                disabled={busy}
                aria-label={`Turn on ${row.label}`}
                onClick={() => turnOn(row.action!)}
                className={buttonClass}
              >
                Turn on
              </button>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}

export function WindowsTools() {
  const [tools, setTools] = useState<ToolLink[] | null>(null);
  const [result, setResult] = useState<OperationResult | string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    bridge.toolsList().then(setTools, () => setTools([]));
  }, []);

  async function repairWinget() {
    setBusy(true);
    try {
      setResult(await bridge.wingetRepair());
    } catch (reason) {
      setResult((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby="tools-title" className="mt-6">
      <h2 id="tools-title" className="mb-2 font-semibold text-muted-foreground">
        Windows tools
      </h2>
      <Outcome result={result} />
      <div className="grid gap-2 lg:grid-cols-2">
        <div className="flex items-center justify-between gap-3 rounded-lg border border-border bg-card p-3">
          <div className="min-w-0">
            <p className="font-medium">Repair winget</p>
            <p className="text-sm text-muted-foreground">Use this if Install apps says winget is missing or broken.</p>
          </div>
          <button type="button" disabled={busy} onClick={repairWinget} className={buttonClass}>
            {busy ? "Working" : "Repair"}
          </button>
        </div>
        {tools?.map((tool) => (
          <div key={tool.id} className="flex items-center justify-between gap-3 rounded-lg border border-border bg-card p-3">
            <div className="min-w-0">
              <p className="font-medium">{tool.title}</p>
              <p className="text-sm text-muted-foreground">{tool.note}</p>
            </div>
            <button
              type="button"
              aria-label={`Open ${tool.title}`}
              onClick={() => bridge.toolsOpen(tool.id).then(setResult, (reason: Error) => setResult(reason.message))}
              className={buttonClass}
            >
              Open
            </button>
          </div>
        ))}
      </div>
    </section>
  );
}

export function ProcessesTab() {
  const [items, setItems] = useState<ProcessEntry[] | null>(null);
  const [query, setQuery] = useState("");
  const [confirm, setConfirm] = useState<number | null>(null);
  const [result, setResult] = useState<OperationResult | string | null>(null);

  const reload = useCallback(() => {
    bridge.processList().then(setItems, (reason: Error) => setResult(reason.message));
  }, []);
  useEffect(reload, [reload]);

  const shown = useMemo(() => (items ?? []).filter((item) => matchesText(item.name, query)), [items, query]);

  async function end(item: ProcessEntry) {
    setConfirm(null);
    try {
      setResult(await bridge.processEnd(item.id, item.name));
      reload();
    } catch (reason) {
      setResult((reason as Error).message);
    }
  }

  return (
    <div>
      <p className="mb-3 text-muted-foreground">
        Running programs, biggest memory use first. Ending one closes it straight away, so unsaved work is lost. Windows' own processes are locked.
      </p>
      <Outcome result={result} />
      <div className="mb-3 flex gap-2">
        <input
          type="search"
          aria-label="Search processes"
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          placeholder="Search"
          className="min-h-11 min-w-0 flex-1 rounded-md border border-border bg-card px-3"
        />
        <button type="button" onClick={reload} className={buttonClass}>
          Refresh
        </button>
      </div>
      {!items && <p className="text-muted-foreground">Reading running programs.</p>}
      <div className="flex flex-col gap-1.5">
        {shown.map((item) => (
          <div key={item.id} className="rounded-lg border border-border bg-card px-4 py-2">
            <div className="flex items-center justify-between gap-3">
              <div className="min-w-0">
                <p className="font-medium break-words">{item.name}</p>
                <p className="text-sm text-muted-foreground">
                  {formatBytes(item.memoryBytes)}
                  {item.hasWindow ? ", has a window" : ""}
                </p>
              </div>
              {item.locked ? (
                <span className="shrink-0 text-sm text-muted-foreground">Locked</span>
              ) : (
                <button type="button" aria-label={`End ${item.name}`} onClick={() => setConfirm(item.id)} className={buttonClass}>
                  End
                </button>
              )}
            </div>
            {confirm === item.id && (
              <div className="mt-2 flex flex-wrap items-center gap-2">
                <span className="font-medium">End {item.name}? Unsaved work will be lost.</span>
                <button type="button" onClick={() => end(item)} className="min-h-11 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary">
                  Yes, end it
                </button>
                <button type="button" onClick={() => setConfirm(null)} className={buttonClass}>
                  Cancel
                </button>
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}
