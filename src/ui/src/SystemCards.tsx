import { FolderOpen } from "lucide-react";
import { useEffect, useState } from "react";
import {
  bridge,
  type DriveWear,
  type MenuEntry,
  type OperationResult,
  type ProcessSignature,
  type ScheduledTaskInfo,
  type SleepBlocker,
  type UpdatePauseStatus,
  type WakeReport,
} from "./bridge";

const cardClass = "rounded-lg border border-border bg-card p-6";
const buttonClass =
  "min-h-11 shrink-0 cursor-pointer rounded-md border border-border px-4 font-medium disabled:cursor-not-allowed disabled:opacity-60";
const primaryClass =
  "min-h-11 shrink-0 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary disabled:cursor-not-allowed disabled:opacity-60";
const rowClass = "rounded-md border border-border bg-background px-3 py-2";

const when = (utc: string) =>
  new Date(utc).toLocaleString(undefined, { day: "numeric", month: "short", hour: "numeric", minute: "2-digit" });

function Message({ result }: { result: OperationResult | string | null }) {
  const text = typeof result === "string" ? result : result?.message;
  const failed = typeof result === "string" || result?.ok === false;
  return <div role="status">{text && <p className={`mt-3 font-medium ${failed ? "text-destructive" : "text-success"}`}>{text}</p>}</div>;
}

function Header({ id, title, note }: { id: string; title: string; note: string }) {
  return (
    <>
      <h2 id={id} className="text-lg font-semibold">
        {title}
      </h2>
      <p className="mt-1 text-muted-foreground">{note}</p>
    </>
  );
}

/** Runs an action, shows its outcome, and reloads afterwards. */
function useAction(reload?: () => unknown) {
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<OperationResult | string | null>(null);
  async function run(key: string, work: () => Promise<OperationResult>) {
    setBusy(key);
    setResult(null);
    try {
      setResult(await work());
      await reload?.();
    } catch (reason) {
      setResult((reason as Error).message);
    } finally {
      setBusy(null);
    }
  }
  return { busy, result, setResult, run };
}

/* ---------------- Monitor ---------------- */

export function wearVerdict(drive: DriveWear) {
  if (drive.wearPercent == null) {
    return null;
  }
  if (drive.wearPercent >= 90) {
    return { tone: "text-destructive", text: "Nearly worn out. Back up your files and plan a replacement." };
  }
  if (drive.wearPercent >= 70) {
    return { tone: "text-warning", text: "Well used. It still works, but keep backups." };
  }
  return { tone: "text-success", text: "Plenty of life left." };
}

export function DriveWearCard() {
  const [drives, setDrives] = useState<DriveWear[] | null>(null);
  const { busy, result, run } = useAction();

  return (
    <section aria-labelledby="wear-title" className={cardClass}>
      <Header id="wear-title" title="Drive wear" note="How much of each SSD's rated life is used, plus temperature and hours, as the drive reports them." />
      <button
        type="button"
        disabled={busy !== null}
        aria-busy={busy !== null}
        onClick={() =>
          run("read", async () => {
            const found = await bridge.driveWear();
            setDrives(found);
            return { ok: true, message: "", restartNeeded: false };
          })
        }
        className={`${buttonClass} mt-3`}
      >
        {busy ? "Reading" : drives ? "Check again" : "Check drive wear"}
      </button>
      <p className="mt-2 text-sm text-muted-foreground">Windows asks for permission, because only admins can read these.</p>
      <Message result={result} />
      {drives?.length === 0 && <p className="mt-3 text-muted-foreground">Your drives don't report wear to Windows.</p>}
      {drives?.map((drive) => {
        const verdict = wearVerdict(drive);
        return (
          <div key={drive.name} className={`${rowClass} mt-3`}>
            <p className="font-semibold">
              {drive.name} <span className="font-normal text-muted-foreground">{drive.media}</span>
            </p>
            <dl className="mt-1 grid grid-cols-[auto_1fr] gap-x-6 gap-y-1 tabular-nums">
              <dt className="text-muted-foreground">Life used</dt>
              <dd>{drive.wearPercent == null ? "Not reported" : `${drive.wearPercent}%`}</dd>
              <dt className="text-muted-foreground">Temperature</dt>
              <dd className={drive.temperatureC != null && drive.temperatureC >= 70 ? "text-warning" : ""}>{drive.temperatureC == null ? "Not reported" : `${drive.temperatureC} °C`}</dd>
              <dt className="text-muted-foreground">Powered on</dt>
              <dd>{drive.powerOnHours == null ? "Not reported" : `${drive.powerOnHours.toLocaleString()} hours`}</dd>
              {drive.readErrors != null && drive.readErrors > 0 && (
                <>
                  <dt className="text-muted-foreground">Read errors</dt>
                  <dd className="text-warning">{drive.readErrors.toLocaleString()}</dd>
                </>
              )}
            </dl>
            {verdict && <p className={`mt-2 text-sm font-medium ${verdict.tone}`}>{verdict.text}</p>}
          </div>
        );
      })}
    </section>
  );
}

const blockerKinds = { display: "Keeps the screen on", system: "Keeps the PC awake", away: "Keeps the PC in away mode" };

export function WakeCard() {
  const [report, setReport] = useState<WakeReport | null>(null);
  const [blockers, setBlockers] = useState<SleepBlocker[] | null>(null);
  const [showAll, setShowAll] = useState(false);
  const load = () => bridge.wakeRead().then(setReport, () => setReport({ wakes: [], devices: [] }));
  const { busy, result, run } = useAction(load);

  useEffect(() => {
    void load();
  }, []);

  const devices = (report?.devices ?? []).filter((device) => showAll || device.armed);
  return (
    <section aria-labelledby="wake-title" className={cardClass}>
      <Header id="wake-title" title="Sleep and wake" note="What woke your PC, which devices are allowed to, and what's stopping it from sleeping." />
      <h3 className="mt-4 font-semibold">Recent wakes</h3>
      {report?.wakes.length === 0 && <p className="text-sm text-muted-foreground">Windows hasn't recorded any wakes from sleep.</p>}
      <ul className="mt-1 flex flex-col gap-1 text-sm">
        {report?.wakes.slice(0, 8).map((wake) => (
          <li key={wake.whenUtc} className="flex justify-between gap-3">
            <span className="min-w-0 truncate">{wake.source}</span>
            <span className="shrink-0 text-muted-foreground">{when(wake.whenUtc)}</span>
          </li>
        ))}
      </ul>

      <h3 className="mt-4 font-semibold">Allowed to wake the PC</h3>
      <p className="text-sm text-muted-foreground">A mouse that gets bumped or a network card can wake the PC by mistake. Turning one off needs admin rights.</p>
      <ul aria-label="Wake devices" className="mt-2 flex flex-col gap-1">
        {devices.map((device) => (
          <li key={device.name} className="flex min-h-11 items-center justify-between gap-3 rounded-md px-2 hover:bg-muted">
            <span className="min-w-0 truncate">{device.name}</span>
            <input
              type="checkbox"
              aria-label={`${device.name} can wake the PC`}
              checked={device.armed}
              disabled={busy !== null}
              onChange={() => run(device.name, () => bridge.wakeDevice(device.name, !device.armed))}
            />
          </li>
        ))}
      </ul>
      {(report?.devices.length ?? 0) > devices.length || showAll ? (
        <button type="button" onClick={() => setShowAll(!showAll)} className="mt-1 min-h-11 cursor-pointer text-sm font-medium text-primary">
          {showAll ? "Show only devices that can wake it" : `Show all ${report?.devices.length} devices`}
        </button>
      ) : null}

      <h3 className="mt-4 font-semibold">Keeping it awake right now</h3>
      <button
        type="button"
        disabled={busy !== null}
        onClick={() =>
          run("blockers", async () => {
            setBlockers(await bridge.sleepBlockers());
            return { ok: true, message: "", restartNeeded: false };
          })
        }
        className={`${buttonClass} mt-2`}
      >
        {busy === "blockers" ? "Checking" : "Check what's keeping it awake"}
      </button>
      {blockers?.length === 0 && <p className="mt-2 text-success">Nothing is stopping your PC from sleeping.</p>}
      <ul className="mt-2 flex flex-col gap-1.5">
        {blockers?.map((blocker) => (
          <li key={blocker.kind + blocker.who} className={rowClass}>
            <p className="font-medium">{blocker.who}</p>
            <p className="text-sm text-muted-foreground">
              {blockerKinds[blocker.kind]}
              {blocker.detail && `: ${blocker.detail}`}
            </p>
          </li>
        ))}
      </ul>
      <Message result={result} />
    </section>
  );
}

/* ---------------- Apps ---------------- */

export function TasksTab() {
  const [tasks, setTasks] = useState<ScheduledTaskInfo[] | null>(null);
  const load = () => bridge.tasksList().then(setTasks, () => setTasks([]));
  const { busy, result, run } = useAction(load);

  useEffect(() => {
    void load();
  }, []);

  return (
    <div>
      <p className="mb-3 text-muted-foreground">
        Tasks programs scheduled to run by themselves, often updaters and helpers that don't show in Startup. Windows' own tasks aren't listed. Changing one needs admin rights.
      </p>
      <Message result={result} />
      {!tasks && <p className="text-muted-foreground">Reading scheduled tasks.</p>}
      {tasks?.length === 0 && <p className="text-muted-foreground">No programs have scheduled tasks.</p>}
      <div className="mt-2 flex flex-col gap-2">
        {tasks?.map((task) => (
          <div key={task.path} className="flex min-h-11 items-center gap-3 rounded-md border border-border bg-card p-3">
            <span className="min-w-0 flex-1">
              <span className="font-medium">{task.name}</span>
              {task.locked && <span className="ml-2 text-sm text-muted-foreground">Locked</span>}
              <span className="block truncate text-sm text-muted-foreground" title={task.program}>
                {task.program || "No program"}
                {task.nextRunUtc && task.enabled && `. Next run ${when(task.nextRunUtc)}`}
              </span>
            </span>
            <input
              type="checkbox"
              aria-label={`${task.name} runs on schedule`}
              checked={task.enabled}
              disabled={busy !== null || task.locked}
              onChange={() => run(task.path, () => bridge.taskSet(task.path, !task.enabled))}
            />
          </div>
        ))}
      </div>
    </div>
  );
}

const menuPlaces = { files: "On files", folders: "On folders", background: "On the desktop and folder background" };

export function MenuTab() {
  const [entries, setEntries] = useState<MenuEntry[] | null>(null);
  const load = () => bridge.menuList().then(setEntries, () => setEntries([]));
  const { busy, result, run } = useAction(load);

  useEffect(() => {
    void load();
  }, []);

  return (
    <div>
      <p className="mb-3 text-muted-foreground">
        Entries other programs added to the right-click menu. On Windows 11 they're under Show more options. Turning one off hides it without uninstalling anything, and History can undo it.
      </p>
      <Message result={result} />
      {!entries && <p className="text-muted-foreground">Reading the menu.</p>}
      {entries?.length === 0 && <p className="text-muted-foreground">No programs have added anything to the right-click menu.</p>}
      {(["files", "folders", "background"] as const).map((where) => {
        const list = (entries ?? []).filter((entry) => entry.where === where);
        return list.length === 0 ? null : (
          <section key={where} className="mt-4">
            <h2 className="mb-2 font-semibold text-muted-foreground">{menuPlaces[where]}</h2>
            <div className="flex flex-col gap-2">
              {list.map((entry) => (
                <label key={entry.id} className="flex min-h-11 cursor-pointer items-center gap-3 rounded-md border border-border bg-card p-3">
                  <span className="min-w-0 flex-1">
                    <span className="font-medium">{entry.name}</span>
                    {entry.file && (
                      <span className="block truncate text-sm text-muted-foreground" title={entry.file}>
                        {entry.file}
                      </span>
                    )}
                  </span>
                  <input
                    type="checkbox"
                    aria-label={`Show ${entry.name} in the menu`}
                    checked={entry.enabled}
                    disabled={busy !== null}
                    onChange={() => run(entry.id, () => bridge.menuSet(entry.id, !entry.enabled))}
                  />
                </label>
              ))}
            </div>
          </section>
        );
      })}
    </div>
  );
}

export function SignatureCard() {
  const [items, setItems] = useState<ProcessSignature[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function check() {
    setBusy(true);
    setError(null);
    try {
      setItems(await bridge.processSignatures());
    } catch (reason) {
      setError((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  const unsigned = items?.filter((item) => item.status === "unsigned") ?? [];
  const signed = items?.filter((item) => item.status === "signed") ?? [];
  return (
    <section aria-labelledby="sig-title" className={`${cardClass} mb-5`}>
      <Header id="sig-title" title="Who made what's running" note="Checks each running program's digital signature. Unsigned isn't always bad, since small tools often aren't signed, but anything you don't recognise is worth a look." />
      <button type="button" disabled={busy} aria-busy={busy} onClick={check} className={`${buttonClass} mt-3`}>
        {busy ? "Checking" : items ? "Check again" : "Check signatures"}
      </button>
      <p className="mt-2 text-sm text-muted-foreground">Games and anti-cheat are skipped, and it won't run while a protected game is open.</p>
      {error && <p role="alert" className="mt-3 font-medium text-destructive">{error}</p>}
      {items && (
        <>
          <h3 className="mt-4 font-semibold">{unsigned.length === 0 ? "Everything running is signed" : `Not signed (${unsigned.length})`}</h3>
          <ul className="mt-1 flex flex-col gap-1.5">
            {unsigned.map((item) => (
              <li key={item.path} className={`${rowClass} flex items-center gap-3`}>
                <span className="min-w-0 flex-1">
                  <span className="font-medium text-warning">{item.name}</span>
                  <span className="block break-all text-sm text-muted-foreground">{item.path}</span>
                </span>
                <button
                  type="button"
                  aria-label={`Show ${item.name} in File Explorer`}
                  title="Show in File Explorer"
                  onClick={() => bridge.spaceShow(item.path).catch(() => undefined)}
                  className="grid size-9 shrink-0 cursor-pointer place-items-center rounded-md hover:bg-muted"
                >
                  <FolderOpen aria-hidden="true" className="size-4" />
                </button>
              </li>
            ))}
          </ul>
          {signed.length > 0 && (
            <details className="mt-3">
              <summary className="min-h-11 cursor-pointer font-medium">Signed ({signed.length})</summary>
              <ul className="mt-1 flex flex-col gap-1 text-sm">
                {signed.map((item) => (
                  <li key={item.path} className="flex justify-between gap-3">
                    <span className="min-w-0 truncate">{item.name}</span>
                    <span className="shrink-0 text-muted-foreground">{item.publisher ?? "Signed"}</span>
                  </li>
                ))}
              </ul>
            </details>
          )}
        </>
      )}
    </section>
  );
}

/* ---------------- Network ---------------- */

export function FirewallCard() {
  const [blocked, setBlocked] = useState<string[] | null>(null);
  const load = () => bridge.firewallList().then((list) => setBlocked(list.map((item) => item.program)), () => setBlocked([]));
  const { busy, result, setResult, run } = useAction(load);

  useEffect(() => {
    void load();
  }, []);

  async function pick() {
    setResult(null);
    try {
      const program = await bridge.firewallPick();
      if (program) {
        await run("block", () => bridge.firewallBlock(program));
      }
    } catch (reason) {
      setResult((reason as Error).message);
    }
  }

  return (
    <section aria-labelledby="fw-title" className={cardClass}>
      <Header id="fw-title" title="Block a program from the internet" note="Adds a Windows Firewall rule so a program can't send or receive anything online. Handy for apps that update or phone home when you don't want them to." />
      <button type="button" disabled={busy !== null} aria-busy={busy === "block"} onClick={pick} className={`${primaryClass} mt-3`}>
        {busy === "block" ? "Blocking" : "Pick a program to block"}
      </button>
      <p className="mt-2 text-sm text-muted-foreground">Windows asks for permission. Windows' own programs, anti-cheat and TuneForge can't be blocked.</p>
      <Message result={result} />
      {blocked && blocked.length > 0 && (
        <ul aria-label="Blocked programs" className="mt-3 flex flex-col gap-1.5">
          {blocked.map((program) => (
            <li key={program} className={`${rowClass} flex items-center gap-3`}>
              <span className="min-w-0 flex-1">
                <span className="font-medium">{program.split("\\").pop()}</span>
                <span className="block break-all text-sm text-muted-foreground">{program}</span>
              </span>
              <button type="button" disabled={busy !== null} onClick={() => run(program, () => bridge.firewallUnblock(program))} className={buttonClass}>
                Unblock
              </button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/* ---------------- Optimize ---------------- */

export function PauseUpdatesCard() {
  const [status, setStatus] = useState<UpdatePauseStatus | null>(null);
  const [weeks, setWeeks] = useState(1);
  const load = () => bridge.pauseStatus().then(setStatus, () => setStatus({ paused: false }));
  const { busy, result, run } = useAction(load);

  useEffect(() => {
    void load();
  }, []);

  return (
    <section aria-labelledby="pause-title" className={`${cardClass} mt-5`}>
      <Header id="pause-title" title="Pause Windows Update" note="Stops updates for up to 5 weeks, the same way Settings does. Useful before a tournament or a deadline. Updates come back on by themselves when the pause ends." />
      {status?.paused && status.untilUtc && (
        <p className="mt-3 font-medium">
          Paused until {new Date(status.untilUtc).toLocaleDateString(undefined, { day: "numeric", month: "long" })}.
        </p>
      )}
      <div className="mt-3 flex flex-wrap items-center gap-2">
        <select aria-label="How long to pause" value={weeks} disabled={busy !== null} onChange={(event) => setWeeks(Number(event.target.value))} className="min-h-11 rounded-md border border-border bg-card px-3">
          {[1, 2, 3, 4, 5].map((count) => (
            <option key={count} value={count}>
              {count === 1 ? "1 week" : `${count} weeks`}
            </option>
          ))}
        </select>
        <button type="button" disabled={busy !== null} onClick={() => run("pause", () => bridge.pauseUpdates(weeks))} className={primaryClass}>
          {status?.paused ? "Change pause" : "Pause updates"}
        </button>
        {status?.paused && (
          <button type="button" disabled={busy !== null} onClick={() => run("resume", () => bridge.pauseUpdates(0))} className={buttonClass}>
            Resume updates
          </button>
        )}
      </div>
      <p className="mt-2 text-sm text-muted-foreground">Windows asks for permission. Security updates are paused too, so don't leave it paused for long.</p>
      <Message result={result} />
    </section>
  );
}

/* ---------------- Repair ---------------- */

export function DriverBackupCard() {
  const [folder, setFolder] = useState<string | null>(null);
  const { busy, result, setResult, run } = useAction();

  async function restore() {
    setResult(null);
    try {
      const picked = await bridge.driversPickFolder();
      if (picked) {
        await run("import", () => bridge.driversImport(picked));
      }
    } catch (reason) {
      setResult((reason as Error).message);
    }
  }

  return (
    <section aria-labelledby="drvbk-title" className={cardClass}>
      <Header id="drvbk-title" title="Back up all drivers" note="Saves every driver on this PC to a folder in Documents, so after reinstalling Windows you can put them all back in one step." />
      <div className="mt-3 flex flex-wrap gap-2">
        <button
          type="button"
          disabled={busy !== null}
          aria-busy={busy === "export"}
          onClick={() =>
            run("export", async () => {
              const done = await bridge.driversExport();
              setFolder(done.folder ?? null);
              return { ok: done.ok, message: done.message, restartNeeded: false };
            })
          }
          className={primaryClass}
        >
          {busy === "export" ? "Saving drivers" : "Back up drivers"}
        </button>
        <button type="button" disabled={busy !== null} aria-busy={busy === "import"} onClick={restore} className={buttonClass}>
          {busy === "import" ? "Installing" : "Install from a backup"}
        </button>
        {folder && (
          <button type="button" onClick={() => bridge.spaceShow(folder).catch(() => undefined)} className={`${buttonClass} inline-flex items-center gap-2`}>
            <FolderOpen aria-hidden="true" className="size-4" />
            Show folder
          </button>
        )}
      </div>
      <p className="mt-2 text-sm text-muted-foreground">Windows asks for permission. Saving takes a few minutes and can use a few GB.</p>
      <Message result={result} />
    </section>
  );
}
