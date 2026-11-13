import { ArrowLeft, File as FileIcon, Folder, FolderOpen } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { bridge, type BootRecord, type BootReport, type DriverCleanState, type GraphicsDriver, type OldDownloadsReport, type SpaceReport, type VolumeInfo } from "./bridge";
import { formatBytes } from "./format";

const buttonClass =
  "min-h-11 shrink-0 cursor-pointer rounded-md border border-border px-4 font-medium disabled:cursor-wait disabled:opacity-60";
const cardClass = "rounded-lg border border-border bg-card p-5";
const RING_SLICES = 6;

/** Shades of the theme's own accent, strongest for the biggest, so the ring works in every theme. */
const shade = (index: number) => `color-mix(in srgb, var(--primary) ${[100, 72, 52, 38, 27, 18][index] ?? 10}%, transparent)`;

function Ring({ report }: { report: SpaceReport }) {
  const total = Math.max(report.bytes, 1);
  const slices = report.items.slice(0, RING_SLICES).map((item) => item.bytes);
  const rest = Math.max(0, report.bytes - slices.reduce((sum, bytes) => sum + bytes, 0));
  let start = 0;
  const stops = [...slices, rest].map((bytes, index) => {
    const end = start + (bytes / total) * 360;
    const stop = `${index < slices.length ? shade(index) : "var(--track)"} ${start}deg ${end}deg`;
    start = end;
    return stop;
  });

  return (
    <div aria-hidden="true" className="grid size-44 shrink-0 place-items-center rounded-full" style={{ background: `conic-gradient(${stops.join(",")})` }}>
      <div className="grid size-28 place-items-center rounded-full bg-card text-center">
        <span className="text-lg font-semibold tabular-nums">{formatBytes(report.bytes)}</span>
      </div>
    </div>
  );
}

/** Shows what takes up space. It only reads, and anything it lists can be opened in File Explorer. */
export function SpaceFinder() {
  const [volumes, setVolumes] = useState<VolumeInfo[]>([]);
  const [trail, setTrail] = useState<string[]>([]);
  const [report, setReport] = useState<SpaceReport | null>(null);
  const [scanning, setScanning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const cache = useRef(new Map<string, SpaceReport>());

  useEffect(() => {
    bridge.storageRead().then(
      (storage) => {
        setVolumes(storage.volumes);
        setTrail((current) => (current.length === 0 && storage.volumes[0] ? [storage.volumes[0].name] : current));
      },
      () => setVolumes([]),
    );
  }, []);

  async function scan(path: string, fresh = false) {
    setError(null);
    const known = cache.current.get(path);
    if (known && !fresh) {
      setReport(known);
      return;
    }

    setScanning(true);
    try {
      const next = await bridge.spaceScan(path);
      if (next) {
        cache.current.set(path, next);
        setReport(next);
      }
    } catch (reason) {
      setError((reason as Error).message);
    } finally {
      setScanning(false);
    }
  }

  function go(next: string[]) {
    setTrail(next);
    void scan(next[next.length - 1]);
  }

  function pickDrive(name: string) {
    cache.current.clear();
    setReport(null);
    setTrail([name]);
  }

  const here = trail[trail.length - 1];
  const largest = Math.max(report?.items[0]?.bytes ?? 1, 1);
  const show = (path: string) => bridge.spaceShow(path).catch((reason: Error) => setError(reason.message));

  return (
    <section aria-labelledby="space-title" className={`${cardClass} mt-5`}>
      <h2 id="space-title" className="font-semibold">
        Space finder
      </h2>
      <p className="mt-1 text-sm text-muted-foreground">See what's using your space. It only looks, and never deletes anything.</p>

      <div className="mt-3 flex flex-wrap items-center gap-2">
        <select
          aria-label="Drive"
          value={trail[0] ?? ""}
          disabled={scanning}
          onChange={(event) => pickDrive(event.target.value)}
          className="min-h-11 rounded-md border border-border bg-card px-3"
        >
          {volumes.map((volume) => (
            <option key={volume.name} value={volume.name}>
              {volume.name} {volume.label}
            </option>
          ))}
        </select>
        {trail.length > 1 && (
          <button type="button" disabled={scanning} onClick={() => go(trail.slice(0, -1))} className={`${buttonClass} inline-flex items-center gap-2`}>
            <ArrowLeft aria-hidden="true" className="size-4" />
            Back
          </button>
        )}
        <p className="min-w-0 flex-1 truncate text-sm text-muted-foreground" title={here}>
          {here}
        </p>
        <button type="button" disabled={scanning || !here} aria-busy={scanning} onClick={() => scan(here, true)} className={buttonClass}>
          {scanning ? "Scanning" : report ? "Scan again" : "Scan"}
        </button>
      </div>

      <div role="status">
        {scanning && <p className="mt-3 text-muted-foreground">Scanning. A whole drive can take a minute or two.</p>}
        {error && <p className="mt-3 font-medium text-destructive">{error}</p>}
      </div>

      {report && !scanning && (
        <div className="mt-4 flex flex-wrap items-start gap-6">
          <Ring report={report} />
          <ul aria-label={`What's using space in ${report.path}`} className="flex min-w-0 flex-1 basis-[28rem] flex-col gap-1.5">
            {report.items.length === 0 && <li className="text-muted-foreground">This folder is empty, or Windows won't let TuneForge look inside.</li>}
            {report.items.map((item, index) => {
              const Icon = item.isFolder ? Folder : FileIcon;
              return (
                <li key={item.path} className="flex items-center gap-3 rounded-md border border-border bg-background px-3 py-1.5">
                  <span aria-hidden="true" className="size-3 shrink-0 rounded-sm" style={{ background: index < RING_SLICES ? shade(index) : "var(--track)" }} />
                  <Icon aria-hidden="true" className="size-4 shrink-0 text-muted-foreground" />
                  {item.isFolder ? (
                    <button type="button" onClick={() => go([...trail, item.path])} className="min-h-9 min-w-0 flex-[2] cursor-pointer truncate text-left font-medium hover:underline" title={item.name}>
                      {item.name}
                    </button>
                  ) : (
                    <span className="min-w-0 flex-[2] truncate" title={item.name}>
                      {item.name}
                    </span>
                  )}
                  <span className="h-2 min-w-0 flex-[3] overflow-hidden rounded-full bg-track">
                    <span className="block h-full rounded-full bg-primary" style={{ width: `${(item.bytes / largest) * 100}%` }} />
                  </span>
                  <span className="w-20 shrink-0 text-right text-sm tabular-nums">{formatBytes(item.bytes)}</span>
                  <button
                    type="button"
                    aria-label={`Show ${item.name} in File Explorer`}
                    title="Show in File Explorer"
                    onClick={() => show(item.path)}
                    className="grid size-9 shrink-0 cursor-pointer place-items-center rounded-md hover:bg-muted"
                  >
                    <FolderOpen aria-hidden="true" className="size-4" />
                  </button>
                </li>
              );
            })}
          </ul>
        </div>
      )}
    </section>
  );
}

/** Big, untouched files in Downloads. Removing sends them to the Recycle Bin, after a confirmation. */
export function OldDownloads() {
  const [report, setReport] = useState<OldDownloadsReport | null>(null);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function scan() {
    setBusy(true);
    setError(null);
    setConfirming(false);
    try {
      setReport(await bridge.oldDownloads());
      setPicked(new Set());
    } catch (reason) {
      setError((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function recycle() {
    setBusy(true);
    setError(null);
    setConfirming(false);
    try {
      const result = await bridge.recycleDownloads([...picked]);
      setMessage(
        `${result.moved} ${result.moved === 1 ? "file" : "files"} moved to the Recycle Bin.${result.failed > 0 ? ` ${result.failed} couldn't be moved, maybe because they're open.` : ""}`,
      );
      setReport(await bridge.oldDownloads());
      setPicked(new Set());
    } catch (reason) {
      setError((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  function toggle(path: string) {
    setMessage(null);
    setPicked((current) => {
      const next = new Set(current);
      if (!next.delete(path)) {
        next.add(path);
      }
      return next;
    });
  }

  const files = report?.files ?? [];
  const pickedBytes = files.filter((file) => picked.has(file.path)).reduce((sum, file) => sum + file.bytes, 0);
  const show = (path: string) => bridge.spaceShow(path).catch((reason: Error) => setError(reason.message));

  return (
    <section aria-labelledby="downloads-title" className={`${cardClass} mt-5`}>
      <h2 id="downloads-title" className="font-semibold">
        Old downloads
      </h2>
      <p className="mt-1 text-sm text-muted-foreground">
        Big files in your Downloads folder that haven't been touched in 3 months. Nothing is removed unless you pick it, and removed files go to the Recycle Bin.
      </p>
      <button type="button" disabled={busy} aria-busy={busy} onClick={scan} className={`${buttonClass} mt-3`}>
        {busy && !report ? "Looking" : report ? "Look again" : "Find old downloads"}
      </button>
      <div role="status">
        {error && <p className="mt-3 font-medium text-destructive">{error}</p>}
        {message && <p className="mt-3 font-medium">{message}</p>}
        {report && files.length === 0 && <p className="mt-3 text-muted-foreground">No big, old files in {report.folder}.</p>}
      </div>
      {files.length > 0 && (
        <>
          <ul aria-label="Old downloads" className="mt-3 flex flex-col gap-1.5">
            {files.map((file) => (
              <li key={file.path} className="flex items-center gap-3 rounded-md border border-border bg-background px-3 py-1.5">
                <input
                  type="checkbox"
                  aria-label={`Pick ${file.name}`}
                  checked={picked.has(file.path)}
                  onChange={() => toggle(file.path)}
                  className="cursor-pointer"
                />
                <span className="min-w-0 flex-1 truncate" title={file.path}>
                  {file.name}
                </span>
                <span className="hidden shrink-0 text-sm text-muted-foreground sm:inline">{Math.round(file.ageDays / 30)} months untouched</span>
                <span className="w-20 shrink-0 text-right text-sm tabular-nums">{formatBytes(file.bytes)}</span>
                <button
                  type="button"
                  aria-label={`Show ${file.name} in File Explorer`}
                  title="Show in File Explorer"
                  onClick={() => show(file.path)}
                  className="grid size-9 shrink-0 cursor-pointer place-items-center rounded-md hover:bg-muted"
                >
                  <FolderOpen aria-hidden="true" className="size-4" />
                </button>
              </li>
            ))}
          </ul>
          <div className="mt-3 flex flex-wrap items-center gap-2">
            {confirming ? (
              <>
                <p className="font-medium">
                  Move {picked.size} {picked.size === 1 ? "file" : "files"} ({formatBytes(pickedBytes)}) to the Recycle Bin?
                </p>
                <button type="button" disabled={busy} onClick={recycle} className="min-h-11 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary">
                  Move to Recycle Bin
                </button>
                <button type="button" onClick={() => setConfirming(false)} className={buttonClass}>
                  Cancel
                </button>
              </>
            ) : (
              <button type="button" disabled={busy || picked.size === 0} onClick={() => setConfirming(true)} className={buttonClass}>
                {picked.size === 0 ? "Pick files to remove" : `Remove ${picked.size} picked (${formatBytes(pickedBytes)})`}
              </button>
            )}
          </div>
        </>
      )}
    </section>
  );
}

const seconds = (ms: number) => `${(ms / 1000).toFixed(1)} s`;
const average = (boots: BootRecord[]) => boots.reduce((sum, boot) => sum + boot.totalMs, 0) / boots.length;

/** Windows times every start itself. Reading those times needs admin rights, so this only runs when asked. */
export function StartupCard() {
  const [report, setReport] = useState<BootReport | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function check() {
    setBusy(true);
    setError(null);
    try {
      setReport(await bridge.bootTimes());
    } catch (reason) {
      setError((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  const last = report?.boots[0];
  const first = report?.firstChangeUtc;
  const before = first ? report!.boots.filter((boot) => boot.whenUtc < first) : [];
  const since = first ? report!.boots.filter((boot) => boot.whenUtc >= first) : [];

  return (
    <section aria-labelledby="startup-title" className={cardClass}>
      <h2 id="startup-title" className="font-semibold">
        Startup time
      </h2>
      <p className="mt-1 text-sm text-muted-foreground">How long Windows takes to start, timed by Windows itself.</p>
      <div role="status">
        {error && <p className="mt-3 font-medium text-destructive">{error}</p>}
        {report && !last && <p className="mt-3 text-muted-foreground">Windows hasn't recorded any startup times on this PC.</p>}
        {last && (
          <>
            <p className="mt-3 text-2xl font-semibold tabular-nums">{seconds(last.totalMs)}</p>
            <p className="text-sm text-muted-foreground">
              Last start. Until the desktop: {seconds(last.mainPathMs)}, then settling in: {seconds(last.afterSignInMs)}.
            </p>
            {before.length > 0 && since.length > 0 && (
              <p className="mt-2 text-sm">
                Before your first TuneForge change: {seconds(average(before))} on average. Since: {seconds(average(since))}.
              </p>
            )}
          </>
        )}
        {report && (report.slowApps?.length ?? 0) > 0 && (
          <div className="mt-4">
            <h3 className="text-sm font-semibold">Slowing your starts</h3>
            <p className="text-sm text-muted-foreground">Apps Windows caught delaying startup in the last 3 months. You can stop them starting with Windows on the Apps page.</p>
            <ul aria-label="Apps slowing startup" className="mt-2 flex flex-col gap-1">
              {report.slowApps!.map((app) => (
                <li key={app.file} className="flex items-baseline justify-between gap-3 text-sm">
                  <span className="min-w-0 truncate" title={app.file}>
                    {app.name}
                  </span>
                  <span className="shrink-0 tabular-nums text-muted-foreground">
                    +{seconds(app.addedMs)}, {app.times} {app.times === 1 ? "time" : "times"}
                  </span>
                </li>
              ))}
            </ul>
          </div>
        )}
        {report && last && report.slowApps?.length === 0 && (
          <p className="mt-3 text-sm text-muted-foreground">Windows hasn't caught any app slowing your starts lately.</p>
        )}
      </div>
      <button type="button" disabled={busy} aria-busy={busy} onClick={check} className={`${buttonClass} mt-3`}>
        {busy ? "Checking" : report ? "Check again" : "Check startup time"}
      </button>
      <p className="mt-2 text-sm text-muted-foreground">Windows asks for permission each time.</p>
    </section>
  );
}

export function DriverCard() {
  const [drivers, setDrivers] = useState<GraphicsDriver[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    bridge.driverRead().then(setDrivers, (reason: Error) => setError(reason.message));
  }, []);

  return (
    <section aria-labelledby="driver-title" className={cardClass}>
      <h2 id="driver-title" className="font-semibold">
        Graphics driver
      </h2>
      <p className="mt-1 text-sm text-muted-foreground">Newer drivers can fix bugs and help games run better.</p>
      <div role="status">
        {error && <p className="mt-3 font-medium text-destructive">{error}</p>}
        {drivers?.length === 0 && <p className="mt-3 text-muted-foreground">No graphics card driver was found.</p>}
      </div>
      {drivers?.map((driver) => (
        <div key={`${driver.name}${driver.version}`} className="mt-3">
          <p className="text-sm text-muted-foreground">{driver.name}</p>
          <p className="text-2xl font-semibold tabular-nums">{driver.friendlyVersion ?? driver.version}</p>
          {driver.dateUtc && (
            <p className={`text-sm ${(driver.ageDays ?? 0) > 180 ? "text-warning" : "text-muted-foreground"}`}>
              From {new Date(driver.dateUtc).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" })}, {driver.ageDays} days old.
              {(driver.ageDays ?? 0) > 180 && " A newer one is likely out."}
            </p>
          )}
          {driver.hasDownloadPage && (
            <button
              type="button"
              onClick={() => bridge.driverPage(driver.vendor).catch((reason: Error) => setError(reason.message))}
              className={`${buttonClass} mt-3`}
            >
              Check {driver.vendor} for a newer one
            </button>
          )}
        </div>
      ))}
      <DriverCleanup />
    </section>
  );
}

type Pending = { kind: "remove" | "delete"; vendor: string } | null;

/** Clean reinstall: back the driver up, remove it with Windows' own tool, and keep a way back. */
function DriverCleanup() {
  const [state, setState] = useState<DriverCleanState | null>(null);
  const [pending, setPending] = useState<Pending>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<{ ok: boolean; message: string; vendor: string; removed: boolean } | null>(null);

  const load = () => bridge.driverPackages().then(setState, () => setState({ packages: [], backups: [] }));

  useEffect(() => {
    void load();
  }, []);

  async function run(kind: "remove" | "restore" | "delete", vendor: string) {
    setPending(null);
    setResult(null);
    setBusy(`${kind}${vendor}`);
    try {
      const action = kind === "remove" ? bridge.driverRemove : kind === "restore" ? bridge.driverRestore : bridge.driverDeleteBackup;
      const outcome = await action(vendor);
      setResult({ ok: outcome.ok, message: outcome.message, vendor, removed: kind === "remove" && outcome.ok });
    } catch (reason) {
      setResult({ ok: false, message: (reason as Error).message, vendor, removed: false });
    } finally {
      setBusy(null);
      void load();
    }
  }

  if (!state || (state.packages.length === 0 && state.backups.length === 0 && !result)) {
    return null;
  }

  const vendors = [...new Set(state.packages.map((item) => item.vendor))];
  return (
    <div className="mt-5 border-t border-border pt-4">
      <h3 className="font-semibold">Clean reinstall</h3>
      <p className="mt-1 text-sm text-muted-foreground">
        Removes the graphics driver completely, so a fresh one installs cleanly. Useful when a driver update causes crashes or glitches.
        TuneForge makes a restore point and a backup first, using Windows' own driver tool.
      </p>

      {vendors.map((vendor) => {
        const count = state.packages.filter((item) => item.vendor === vendor).length;
        const confirming = pending?.kind === "remove" && pending.vendor === vendor;
        return (
          <div key={vendor} className="mt-3">
            {confirming ? (
              <div role="alertdialog" aria-labelledby={`remove-${vendor}`} className="rounded-md border border-warning p-4">
                <p id={`remove-${vendor}`} className="font-semibold">
                  Remove the {vendor} driver?
                </p>
                <ul className="mt-2 ml-5 list-disc text-sm">
                  <li>Close your games first. The screen flickers and drops to a low resolution until a new driver is installed.</li>
                  <li>Download the new driver from {vendor} before you start.</li>
                  <li>
                    Removes {count} driver {count === 1 ? "package" : "packages"}, including older leftovers. A backup of {count === 1 ? "it" : "them"} is kept, so you can put it back.
                  </li>
                </ul>
                <div className="mt-3 flex flex-wrap gap-2">
                  <button type="button" onClick={() => run("remove", vendor)} className="min-h-11 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary">
                    Yes, remove it
                  </button>
                  <button type="button" onClick={() => setPending(null)} className={buttonClass}>
                    Cancel
                  </button>
                </div>
              </div>
            ) : (
              <button
                type="button"
                disabled={busy !== null}
                aria-busy={busy === `remove${vendor}`}
                onClick={() => setPending({ kind: "remove", vendor })}
                className={buttonClass}
              >
                {busy === `remove${vendor}` ? "Backing up and removing" : `Remove ${vendor} driver`}
              </button>
            )}
          </div>
        );
      })}

      <div role="status">
        {busy?.startsWith("remove") && <p className="mt-2 text-sm text-muted-foreground">This takes a few minutes. Your screen will flicker.</p>}
        {result && (
          <div className="mt-3">
            <p className={`font-medium ${result.ok ? "" : "text-destructive"}`}>{result.message}</p>
            {result.removed && (
              <button type="button" onClick={() => bridge.driverPage(result.vendor).catch(() => undefined)} className={`${buttonClass} mt-2`}>
                Get the new {result.vendor} driver
              </button>
            )}
          </div>
        )}
      </div>

      {state.backups.map((backup) => {
        const deleting = pending?.kind === "delete" && pending.vendor === backup.vendor;
        return (
          <div key={backup.vendor} className="mt-4 rounded-md border border-border p-3">
            <p className="text-sm">
              A backup of your old {backup.vendor} driver is kept ({formatBytes(backup.bytes)}). Put it back if the new one gives you trouble.
            </p>
            <div className="mt-2 flex flex-wrap items-center gap-2">
              {deleting ? (
                <>
                  <p className="text-sm font-medium">Delete the backup? You won't be able to put this driver back.</p>
                  <button type="button" onClick={() => run("delete", backup.vendor)} className={buttonClass}>
                    Yes, delete it
                  </button>
                  <button type="button" onClick={() => setPending(null)} className={buttonClass}>
                    Cancel
                  </button>
                </>
              ) : (
                <>
                  <button type="button" disabled={busy !== null} aria-busy={busy === `restore${backup.vendor}`} onClick={() => run("restore", backup.vendor)} className={buttonClass}>
                    {busy === `restore${backup.vendor}` ? "Putting it back" : "Put it back"}
                  </button>
                  <button type="button" disabled={busy !== null} onClick={() => setPending({ kind: "delete", vendor: backup.vendor })} className={buttonClass}>
                    Delete backup
                  </button>
                </>
              )}
            </div>
          </div>
        );
      })}
    </div>
  );
}
