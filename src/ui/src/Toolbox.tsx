import { FolderOpen } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import {
  bridge,
  type BackgroundAppsReport,
  type ChangesReport,
  type ConnectionLogStatus,
  type CrashRecord,
  type DelayedStartup,
  type DiskSpeedResult,
  type DuplicateReport,
  type HostsEntry,
  type LeftoverFolder,
  type LinkInfo,
  type OperationResult,
  type PermissionUse,
  type RestorePointReport,
  type VolumeInfo,
  type WifiScan,
} from "./bridge";
import { formatBytes } from "./format";

const cardClass = "rounded-lg border border-border bg-card p-6";
const buttonClass =
  "min-h-11 shrink-0 cursor-pointer rounded-md border border-border px-4 font-medium disabled:cursor-not-allowed disabled:opacity-60";
const primaryClass =
  "min-h-11 shrink-0 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary disabled:cursor-not-allowed disabled:opacity-60";
const rowClass = "rounded-md border border-border bg-background px-3 py-2";

const when = (utc: string) =>
  new Date(utc).toLocaleString(undefined, { day: "numeric", month: "short", year: "numeric", hour: "numeric", minute: "2-digit" });
const day = (utc: string) => new Date(utc).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });

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

/* ---------------- Repair ---------------- */

export function CrashCard() {
  const [crashes, setCrashes] = useState<CrashRecord[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    bridge.crashList().then(setCrashes, (reason: Error) => setError(reason.message));
  }, []);

  return (
    <section aria-labelledby="crash-title" className={cardClass}>
      <Header id="crash-title" title="Blue screens and crashes" note="Every blue screen and sudden restart in the last 90 days, with what the stop code usually means." />
      <div role="status">
        {!crashes && !error && <p className="mt-3 text-muted-foreground">Reading Windows' crash records.</p>}
        {error && <p className="mt-3 font-medium text-destructive">{error}</p>}
        {crashes?.length === 0 && <p className="mt-3 text-success">No blue screens or sudden restarts in the last 90 days.</p>}
      </div>
      {crashes && crashes.length > 0 && (
        <ul aria-label="Crashes" className="mt-3 flex flex-col gap-2">
          {crashes.map((crash) => (
            <li key={crash.whenUtc + crash.kind} className={rowClass}>
              <p className="flex flex-wrap items-baseline justify-between gap-x-3">
                <span className="font-semibold">
                  {crash.name}
                  {crash.code && <span className="ml-2 font-normal text-muted-foreground tabular-nums">{crash.code}</span>}
                </span>
                <span className="text-sm text-muted-foreground">{when(crash.whenUtc)}</span>
              </p>
              <p className="mt-1 text-sm">{crash.meaning}</p>
              {crash.dumpPath && <p className="mt-1 break-all text-sm text-muted-foreground">Crash file: {crash.dumpPath}</p>}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

export function RestorePointsCard() {
  const [report, setReport] = useState<RestorePointReport | null>(null);
  const [busy, setBusy] = useState(false);
  const [confirm, setConfirm] = useState<number | null>(null);
  const [result, setResult] = useState<OperationResult | string | null>(null);

  async function load() {
    setBusy(true);
    setResult(null);
    try {
      setReport(await bridge.restorePoints());
    } catch (reason) {
      setResult((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function remove(sequence: number) {
    setConfirm(null);
    setBusy(true);
    try {
      setResult(await bridge.restorePointDelete(sequence));
      setReport(await bridge.restorePoints());
    } catch (reason) {
      setResult((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby="points-title" className={cardClass}>
      <Header id="points-title" title="Restore points" note="Snapshots Windows can roll back to. Old ones take up space, and deleting one can't be undone." />
      <button type="button" disabled={busy} aria-busy={busy} onClick={load} className={`${buttonClass} mt-3`}>
        {busy && !report ? "Reading" : report ? "Check again" : "Show restore points"}
      </button>
      <p className="mt-2 text-sm text-muted-foreground">Windows asks for permission, because only admins can see these.</p>
      <Message result={result} />
      {report && (
        <>
          {report.maxBytes > 0 && (
            <p className="mt-3 tabular-nums">
              Using {formatBytes(report.usedBytes)} of the {formatBytes(report.maxBytes)} Windows allows.
            </p>
          )}
          {report.points.length === 0 && <p className="mt-3 text-muted-foreground">There are no restore points. System Restore may be off.</p>}
          <ul aria-label="Restore points" className="mt-3 flex flex-col gap-2">
            {report.points.map((point, index) => (
              <li key={point.sequence} className={`${rowClass} flex flex-wrap items-center justify-between gap-3`}>
                <span className="min-w-0">
                  <span className="font-medium">{point.description}</span>
                  <span className="block text-sm text-muted-foreground">
                    {point.createdUtc ? when(point.createdUtc) : "Unknown date"}
                    {index === 0 && ", the newest"}
                  </span>
                </span>
                {confirm === point.sequence ? (
                  <span className="flex flex-wrap items-center gap-2">
                    <span className="text-sm font-medium">Delete it for good?</span>
                    <button type="button" onClick={() => remove(point.sequence)} className={buttonClass}>
                      Yes, delete
                    </button>
                    <button type="button" onClick={() => setConfirm(null)} className={buttonClass}>
                      Cancel
                    </button>
                  </span>
                ) : (
                  <button type="button" disabled={busy} aria-label={`Delete ${point.description}`} onClick={() => setConfirm(point.sequence)} className={buttonClass}>
                    Delete
                  </button>
                )}
              </li>
            ))}
          </ul>
        </>
      )}
    </section>
  );
}

export function IconCacheCard() {
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<OperationResult | string | null>(null);

  async function run() {
    setConfirming(false);
    setBusy(true);
    try {
      setResult(await bridge.iconCacheRebuild());
    } catch (reason) {
      setResult((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby="icons-title" className={cardClass}>
      <Header id="icons-title" title="Fix icons and thumbnails" note="Rebuilds Explorer's icon and thumbnail caches, for blank, wrong or old-looking icons." />
      {confirming ? (
        <div className="mt-3 flex flex-wrap items-center gap-2">
          <span className="font-medium">The taskbar and open folders close for a few seconds. Continue?</span>
          <button type="button" onClick={run} className={primaryClass}>
            Yes, rebuild
          </button>
          <button type="button" onClick={() => setConfirming(false)} className={buttonClass}>
            Cancel
          </button>
        </div>
      ) : (
        <button type="button" disabled={busy} aria-busy={busy} onClick={() => setConfirming(true)} className={`${buttonClass} mt-3`}>
          {busy ? "Rebuilding" : "Rebuild icons"}
        </button>
      )}
      <Message result={result} />
    </section>
  );
}

/* ---------------- Network ---------------- */

const speedText = (bps: number) => (bps >= 1e9 ? `${+(bps / 1e9).toFixed(1)} Gbps` : `${Math.round(bps / 1e6)} Mbps`);

export function LinkSpeedCard() {
  const [links, setLinks] = useState<LinkInfo[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    bridge.linkRead().then(setLinks, (reason: Error) => setError(reason.message));
  }, []);

  return (
    <section aria-labelledby="link-title" className={cardClass}>
      <Header id="link-title" title="Link speed" note="The speed your network adapter agreed with the router. Your internet can never be faster than this." />
      <div role="status">
        {error && <p className="mt-3 font-medium text-destructive">{error}</p>}
        {links?.length === 0 && <p className="mt-3 text-muted-foreground">No connected network adapter was found.</p>}
      </div>
      {links?.map((link) => {
        const slowCable = link.kind === "Ethernet" && link.speedBps > 0 && link.speedBps < 1e9;
        return (
          <div key={link.name} className="mt-3">
            <p className="text-sm text-muted-foreground">
              {link.kind}
              {link.name !== link.kind && `, ${link.name}`}
            </p>
            <p className={`text-2xl font-semibold tabular-nums ${slowCable ? "text-warning" : ""}`}>{speedText(link.speedBps)}</p>
            {slowCable && (
              <p className="text-sm">
                Most routers and PCs can do 1 Gbps. Running slower usually means a damaged cable, a loose plug, or an old cable with only 4 wires. Try a Cat5e or Cat6 cable.
              </p>
            )}
            {link.kind === "Wi-Fi" && <p className="text-sm text-muted-foreground">Wi-Fi speed changes with distance and walls. Run the scan below to check your channel.</p>}
          </div>
        );
      })}
    </section>
  );
}

/** For 2.4 GHz, only channels 1, 6 and 11 don't overlap, so the advice picks the quietest of those. */
export function quietestChannel(scan: WifiScan): number | null {
  const near = scan.networks.filter((network) => network.band === "2.4 GHz" && !network.connected);
  if (scan.networks.every((network) => network.band !== "2.4 GHz")) {
    return null;
  }
  const crowd = (channel: number) =>
    near.filter((network) => Math.abs(network.channel - channel) < 5).reduce((sum, network) => sum + network.signalPercent, 0);
  return [1, 6, 11].sort((a, b) => crowd(a) - crowd(b))[0];
}

export function WifiCard() {
  const [scan, setScan] = useState<WifiScan | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function run() {
    setBusy(true);
    setError(null);
    try {
      setScan(await bridge.wifiScan());
    } catch (reason) {
      setError((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  const mine = scan?.networks.find((network) => network.connected);
  const best = scan ? quietestChannel(scan) : null;
  return (
    <section aria-labelledby="wifi-title" className={cardClass}>
      <Header id="wifi-title" title="Wi-Fi channels" note="Lists nearby Wi-Fi and how crowded each channel is, so you can pick a quieter one in your router's settings." />
      <button type="button" disabled={busy} aria-busy={busy} onClick={run} className={`${buttonClass} mt-3`}>
        {busy ? "Scanning" : scan ? "Scan again" : "Scan nearby Wi-Fi"}
      </button>
      <div role="status">
        {error && <p className="mt-3 font-medium text-destructive">{error}</p>}
        {scan && !scan.hasWifi && <p className="mt-3 text-muted-foreground">This PC has no Wi-Fi adapter.</p>}
        {scan?.problem && <p className="mt-3 font-medium text-warning">{scan.problem}</p>}
      </div>
      {scan && scan.networks.length > 0 && (
        <>
          {mine && best !== null && mine.band === "2.4 GHz" && mine.channel !== best && (
            <p className="mt-3 font-medium">Your network is on channel {mine.channel}. Channel {best} looks quieter.</p>
          )}
          {mine && best !== null && mine.band === "2.4 GHz" && mine.channel === best && <p className="mt-3 text-success">Your network is already on the quietest channel.</p>}
          <table className="mt-3 w-full text-left text-sm tabular-nums">
            <thead className="text-muted-foreground">
              <tr>
                <th scope="col" className="pr-4 font-medium">Network</th>
                <th scope="col" className="pr-4 font-medium">Channel</th>
                <th scope="col" className="pr-4 font-medium">Band</th>
                <th scope="col" className="font-medium">Signal</th>
              </tr>
            </thead>
            <tbody>
              {scan.networks.slice(0, 40).map((network, index) => (
                <tr key={`${network.name}${network.channel}${index}`} className={network.connected ? "font-semibold" : ""}>
                  <td className="pr-4">
                    {network.name}
                    {network.connected && " (yours)"}
                  </td>
                  <td className="pr-4">{network.channel}</td>
                  <td className="pr-4">{network.band}</td>
                  <td>{network.signalPercent}%</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </section>
  );
}

export function ConnectionLogCard() {
  const [status, setStatus] = useState<ConnectionLogStatus | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    const read = () => bridge.connectionLog().then((next) => live && setStatus(next), (reason: Error) => live && setError(reason.message));
    void read();
    const timer = setInterval(read, 3000);
    return () => {
      live = false;
      clearInterval(timer);
    };
  }, []);

  async function toggle() {
    setError(null);
    try {
      setStatus(await bridge.connectionLogSet(!status?.running));
    } catch (reason) {
      setError((reason as Error).message);
    }
  }

  const events = status?.events ?? [];
  const count = (where: string, kind: string) => events.filter((item) => item.where === where && item.kind === kind).length;
  return (
    <section aria-labelledby="connlog-title" className={cardClass}>
      <Header
        id="connlog-title"
        title="Connection logger"
        note="Pings your router and the internet every second, and writes down every drop and spike with the time. Leave it running to catch problems that come and go."
      />
      <div className="mt-3 flex flex-wrap items-center gap-2">
        <button type="button" disabled={!status} onClick={toggle} className={status?.running ? buttonClass : primaryClass}>
          {status?.running ? "Stop logging" : "Start logging"}
        </button>
        <button
          type="button"
          onClick={() => bridge.connectionLogOpen().catch((reason: Error) => setError(reason.message))}
          className={`${buttonClass} inline-flex items-center gap-2`}
        >
          <FolderOpen aria-hidden="true" className="size-4" />
          Show log file
        </button>
      </div>
      <p className="mt-2 text-sm text-muted-foreground">It keeps going in the tray and starts again with TuneForge until you stop it. It sends only tiny pings.</p>
      <div role="status">
        {error && <p className="mt-3 font-medium text-destructive">{error}</p>}
        {status?.running && status.sinceUtc && (
          <p className="mt-3">
            Logging since {when(status.sinceUtc)}.{" "}
            {status.router ? `Watching your router (${status.router}) and the internet.` : "Your router wasn't found, so only the internet is watched."}
          </p>
        )}
      </div>
      {status && (status.running || events.length > 0) && (
        <>
          <dl className="mt-3 grid grid-cols-[auto_1fr] gap-x-6 gap-y-1 tabular-nums">
            <dt className="text-muted-foreground">Router drops</dt>
            <dd>{count("router", "drop")}</dd>
            <dt className="text-muted-foreground">Internet drops</dt>
            <dd>{count("internet", "drop")}</dd>
            <dt className="text-muted-foreground">Spikes</dt>
            <dd>{count("router", "spike") + count("internet", "spike")}</dd>
          </dl>
          {count("router", "drop") > 0 && <p className="mt-2 text-sm">Drops at the router are inside your home: Wi-Fi signal, a cable or the router itself.</p>}
          {count("internet", "drop") > 0 && count("router", "drop") === 0 && (
            <p className="mt-2 text-sm">Your router stayed up while the internet dropped, which points to your internet provider. The log file is proof you can send them.</p>
          )}
          {events.length > 0 && (
            <ul aria-label="Connection problems" className="mt-3 flex flex-col gap-1 text-sm tabular-nums">
              {events.slice(0, 30).map((item, index) => (
                <li key={`${item.whenUtc}${item.where}${index}`} className="flex justify-between gap-3">
                  <span>
                    {item.kind === "drop" ? `${item.where === "router" ? "Router" : "Internet"} dropped for ${item.seconds} s` : `${item.where === "router" ? "Router" : "Internet"} spike, ${item.ms} ms`}
                  </span>
                  <span className="text-muted-foreground">{when(item.whenUtc)}</span>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </section>
  );
}

export function HostsCard() {
  const [entries, setEntries] = useState<HostsEntry[] | null>(null);
  const [hasBackup, setHasBackup] = useState(false);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<OperationResult | string | null>(null);

  const load = () =>
    bridge.hostsRead().then(
      (report) => {
        setEntries(report.entries);
        setHasBackup(report.hasBackup);
        setPicked(new Set());
      },
      (reason: Error) => setResult(reason.message),
    );

  useEffect(() => {
    void load();
  }, []);

  async function act(work: () => Promise<OperationResult>) {
    setConfirming(false);
    setBusy(true);
    try {
      setResult(await work());
      await load();
    } catch (reason) {
      setResult((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby="hosts-title" className={cardClass}>
      <Header
        id="hosts-title"
        title="Hosts file"
        note="Lines here send website names to fixed addresses before DNS is asked. Ad blockers and some apps add them; so can unwanted software."
      />
      {entries?.length === 0 && <p className="mt-3 text-muted-foreground">Your hosts file has no entries. That's normal.</p>}
      {entries && entries.length > 0 && (
        <ul aria-label="Hosts entries" className="mt-3 flex flex-col gap-1.5">
          {entries.map((entry) => (
            <li key={entry.text} className={`${rowClass} flex items-center gap-3`}>
              <input
                type="checkbox"
                aria-label={`Pick ${entry.names.join(", ")}`}
                checked={picked.has(entry.text)}
                onChange={() =>
                  setPicked((current) => {
                    const next = new Set(current);
                    if (!next.delete(entry.text)) {
                      next.add(entry.text);
                    }
                    return next;
                  })
                }
              />
              <span className="min-w-0 flex-1">
                <span className="block break-all font-medium">{entry.names.join(", ")}</span>
                <span className="block text-sm text-muted-foreground">
                  {entry.blocks ? "Blocked" : `Sent to ${entry.address}`}
                  {entry.note && `. Note: ${entry.note}`}
                </span>
              </span>
            </li>
          ))}
        </ul>
      )}
      <div className="mt-3 flex flex-wrap items-center gap-2">
        {confirming ? (
          <>
            <span className="font-medium">Remove {picked.size} {picked.size === 1 ? "line" : "lines"}? A copy is saved first.</span>
            <button type="button" onClick={() => act(() => bridge.hostsRemove([...picked]))} className={primaryClass}>
              Yes, remove
            </button>
            <button type="button" onClick={() => setConfirming(false)} className={buttonClass}>
              Cancel
            </button>
          </>
        ) : (
          entries &&
          entries.length > 0 && (
            <button type="button" disabled={busy || picked.size === 0} onClick={() => setConfirming(true)} className={buttonClass}>
              {picked.size === 0 ? "Pick lines to remove" : `Remove ${picked.size} picked`}
            </button>
          )
        )}
        {hasBackup && !confirming && (
          <button type="button" disabled={busy} onClick={() => act(bridge.hostsRestore)} className={buttonClass}>
            Undo last change
          </button>
        )}
      </div>
      {(confirming || picked.size > 0) && <p className="mt-2 text-sm text-muted-foreground">Windows asks for permission, because the hosts file is protected.</p>}
      <Message result={result} />
    </section>
  );
}

/* ---------------- Testers ---------------- */

export function speedVerdict(result: DiskSpeedResult) {
  if (result.randomReadIops < 400) {
    return "This behaves like a hard drive. Games and Windows load much faster from an SSD.";
  }
  if (result.readMBps >= 1500) {
    return "NVMe SSD speed. That's as fast as drives get.";
  }
  if (result.readMBps >= 350) {
    return "SATA SSD speed. Good for games and Windows.";
  }
  return "Slower than a typical SSD. If this is an NVMe drive, check it's in the right slot and the drive isn't nearly full.";
}

export function DiskSpeedTest() {
  const [volumes, setVolumes] = useState<VolumeInfo[]>([]);
  const [drive, setDrive] = useState("");
  const [busy, setBusy] = useState(false);
  const [results, setResults] = useState<DiskSpeedResult[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    bridge.storageRead().then(
      (storage) => {
        setVolumes(storage.volumes);
        setDrive((current) => current || storage.volumes[0]?.name || "");
      },
      () => setVolumes([]),
    );
  }, []);

  async function run() {
    setBusy(true);
    setError(null);
    try {
      const result = await bridge.diskSpeed(drive);
      setResults((current) => [result, ...current.filter((item) => item.drive !== result.drive)]);
    } catch (reason) {
      setError((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby="disk-title" className={cardClass}>
      <Header
        id="disk-title"
        title="Drive speed"
        note="Writes and reads a 512 MB test file, straight to the drive, then deletes it. It takes about 20 seconds."
      />
      <div className="mt-3 flex flex-wrap items-center gap-2">
        <select aria-label="Drive to test" value={drive} disabled={busy} onChange={(event) => setDrive(event.target.value)} className="min-h-11 rounded-md border border-border bg-card px-3">
          {volumes.map((volume) => (
            <option key={volume.name} value={volume.name}>
              {volume.name} {volume.label}
            </option>
          ))}
        </select>
        <button type="button" disabled={busy || !drive} aria-busy={busy} onClick={run} className={primaryClass}>
          {busy ? "Testing" : "Test speed"}
        </button>
      </div>
      <div role="status">
        {busy && <p className="mt-3 text-muted-foreground">Testing. Avoid using the drive until it finishes.</p>}
        {error && <p className="mt-3 font-medium text-destructive">{error}</p>}
      </div>
      {results.map((result) => (
        <div key={result.drive} className={`${rowClass} mt-3`}>
          <p className="font-semibold">{result.drive}</p>
          <dl className="mt-1 grid grid-cols-[auto_1fr] gap-x-6 gap-y-1 tabular-nums">
            <dt className="text-muted-foreground">Read</dt>
            <dd>{result.readMBps} MB/s</dd>
            <dt className="text-muted-foreground">Write</dt>
            <dd>{result.writeMBps} MB/s</dd>
            <dt className="text-muted-foreground">Small random reads</dt>
            <dd>{result.randomReadIops.toLocaleString()} per second</dd>
          </dl>
          <p className="mt-2 text-sm">{speedVerdict(result)}</p>
        </div>
      ))}
    </section>
  );
}

/** Speaker names in the order Windows sends audio channels. */
export function speakerNames(count: number): string[] {
  if (count >= 8) {
    return ["Front left", "Front right", "Center", "Subwoofer", "Rear left", "Rear right", "Side left", "Side right"];
  }
  if (count >= 6) {
    return ["Front left", "Front right", "Center", "Subwoofer", "Rear left", "Rear right"];
  }
  if (count >= 4) {
    return ["Front left", "Front right", "Rear left", "Rear right"];
  }
  return ["Left", "Right"];
}

export function SpeakerTest() {
  const [names, setNames] = useState<string[]>([]);
  const [playing, setPlaying] = useState<number | null>(null);

  useEffect(() => {
    try {
      const audio = new AudioContext();
      setNames(speakerNames(audio.destination.maxChannelCount));
      void audio.close();
    } catch {
      setNames(speakerNames(2));
    }
  }, []);

  function play(channel: number) {
    const audio = new AudioContext();
    const count = audio.destination.maxChannelCount >= names.length ? names.length : 2;
    audio.destination.channelCount = count;
    audio.destination.channelCountMode = "explicit";
    audio.destination.channelInterpretation = "discrete";
    const merger = audio.createChannelMerger(count);
    const oscillator = audio.createOscillator();
    const gain = audio.createGain();
    // The subwoofer only plays low notes.
    oscillator.frequency.value = names[channel] === "Subwoofer" ? 60 : 440;
    gain.gain.value = 0.2;
    oscillator.connect(gain).connect(merger, 0, channel);
    merger.connect(audio.destination);
    oscillator.start();
    oscillator.stop(audio.currentTime + 1);
    setPlaying(channel);
    oscillator.onended = () => {
      setPlaying(null);
      void audio.close();
    };
  }

  return (
    <section aria-labelledby="speakers-title" className={cardClass}>
      <Header
        id="speakers-title"
        title="Speakers"
        note="Plays a tone through one speaker at a time, so you can check each is wired to the right place. Surround speakers only show when Windows is set up for them."
      />
      <div className="mt-3 flex flex-wrap gap-2">
        {names.map((name, index) => (
          <button key={name} type="button" aria-pressed={playing === index} onClick={() => play(index)} className={playing === index ? primaryClass : buttonClass}>
            {name}
          </button>
        ))}
      </div>
    </section>
  );
}

/* ---------------- Cleanup and Apps ---------------- */

export function DuplicatesCard() {
  const [volumes, setVolumes] = useState<VolumeInfo[]>([]);
  const [where, setWhere] = useState("personal");
  const [report, setReport] = useState<DuplicateReport | null>(null);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [result, setResult] = useState<OperationResult | string | null>(null);

  useEffect(() => {
    bridge.storageRead().then((storage) => setVolumes(storage.volumes.filter((volume) => !volume.name.toUpperCase().startsWith("C:"))), () => setVolumes([]));
  }, []);

  async function scan() {
    setBusy(true);
    setResult(null);
    setConfirming(false);
    try {
      const next = await bridge.duplicatesScan(where);
      if (next) {
        setReport(next);
        setPicked(new Set());
      }
    } catch (reason) {
      setResult((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  /** A file can be picked only while another copy in its group stays. */
  function toggle(group: DuplicateReport["groups"][number], path: string) {
    setPicked((current) => {
      const next = new Set(current);
      if (next.has(path)) {
        next.delete(path);
      } else if (group.files.filter((file) => !next.has(file.path)).length > 1) {
        next.add(path);
      }
      return next;
    });
  }

  const picks = useMemo(
    () =>
      (report?.groups ?? []).flatMap((group) => {
        const keep = group.files.find((file) => !picked.has(file.path));
        return keep ? group.files.filter((file) => picked.has(file.path)).map((file) => ({ path: file.path, keep: keep.path, bytes: group.bytes })) : [];
      }),
    [report, picked],
  );
  const pickedBytes = picks.reduce((sum, pick) => sum + pick.bytes, 0);

  async function recycle() {
    setConfirming(false);
    setBusy(true);
    try {
      const done = await bridge.duplicatesRecycle(picks.map(({ path, keep }) => ({ path, keep })));
      setResult({
        ok: done.failed === 0,
        message: `${done.moved} ${done.moved === 1 ? "file" : "files"} moved to the Recycle Bin.${done.failed > 0 ? ` ${done.failed} were left, because they changed or are open.` : ""}`,
        restartNeeded: false,
      });
      const next = await bridge.duplicatesScan(where);
      if (next) {
        setReport(next);
      }
      setPicked(new Set());
    } catch (reason) {
      setResult((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby="dupes-title" className={`${cardClass} mt-5`}>
      <Header
        id="dupes-title"
        title="Duplicate files"
        note="Finds files over 10 MB that are exactly the same. Game libraries and program folders are skipped, and one copy of each file always stays."
      />
      <div className="mt-3 flex flex-wrap items-center gap-2">
        <select aria-label="Where to look" value={where} disabled={busy} onChange={(event) => setWhere(event.target.value)} className="min-h-11 rounded-md border border-border bg-card px-3">
          <option value="personal">Your folders (Desktop, Documents, Downloads, Pictures, Videos, Music)</option>
          {volumes.map((volume) => (
            <option key={volume.name} value={volume.name}>
              Drive {volume.name} {volume.label}
            </option>
          ))}
        </select>
        <button type="button" disabled={busy} aria-busy={busy} onClick={scan} className={buttonClass}>
          {busy && !confirming ? "Looking" : "Find duplicates"}
        </button>
      </div>
      {busy && <p className="mt-3 text-muted-foreground">Comparing files. A big drive can take a few minutes.</p>}
      <Message result={result} />
      {report && report.groups.length === 0 && <p className="mt-3 text-muted-foreground">No duplicates over 10 MB were found.</p>}
      {report && report.groups.length > 0 && (
        <>
          <p className="mt-3">
            {report.groups.length} sets of duplicates. Removing the extra copies would free {formatBytes(report.wastedBytes)}.
          </p>
          <ul aria-label="Duplicates" className="mt-3 flex flex-col gap-2">
            {report.groups.map((group) => (
              <li key={group.files[0].path} className={rowClass}>
                <p className="text-sm font-semibold">
                  {group.files.length} copies of {formatBytes(group.bytes)}
                </p>
                <ul className="mt-1 flex flex-col gap-1">
                  {group.files.map((file) => (
                    <li key={file.path} className="flex items-center gap-3 text-sm">
                      <input
                        type="checkbox"
                        aria-label={`Remove ${file.path}`}
                        checked={picked.has(file.path)}
                        disabled={!picked.has(file.path) && group.files.filter((other) => !picked.has(other.path)).length <= 1}
                        onChange={() => toggle(group, file.path)}
                      />
                      <span className="min-w-0 flex-1 break-all">{file.path}</span>
                      <span className="shrink-0 text-muted-foreground">{day(file.modifiedUtc)}</span>
                    </li>
                  ))}
                </ul>
              </li>
            ))}
          </ul>
          <div className="mt-3 flex flex-wrap items-center gap-2">
            {confirming ? (
              <>
                <span className="font-medium">
                  Move {picks.length} {picks.length === 1 ? "file" : "files"} ({formatBytes(pickedBytes)}) to the Recycle Bin?
                </span>
                <button type="button" onClick={recycle} className={primaryClass}>
                  Move to Recycle Bin
                </button>
                <button type="button" onClick={() => setConfirming(false)} className={buttonClass}>
                  Cancel
                </button>
              </>
            ) : (
              <button type="button" disabled={busy || picks.length === 0} onClick={() => setConfirming(true)} className={buttonClass}>
                {picks.length === 0 ? "Pick copies to remove" : `Remove ${picks.length} picked (${formatBytes(pickedBytes)})`}
              </button>
            )}
          </div>
        </>
      )}
    </section>
  );
}

/** Shown after an uninstall: folders the program left behind. */
export function LeftoversPanel({ name }: { name: string }) {
  const [folders, setFolders] = useState<LeftoverFolder[] | null>(null);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [result, setResult] = useState<OperationResult | string | null>(null);

  useEffect(() => {
    bridge.leftoversFind(name).then(
      (found) => {
        setFolders(found);
        setPicked(new Set(found.filter((folder) => folder.canRemove).map((folder) => folder.path)));
      },
      () => setFolders([]),
    );
  }, [name]);

  async function recycle() {
    try {
      const done = await bridge.leftoversRecycle(name, [...picked]);
      setResult({
        ok: done.failed === 0,
        message: `${done.moved} ${done.moved === 1 ? "folder" : "folders"} moved to the Recycle Bin.${done.failed > 0 ? ` ${done.failed} couldn't be moved.` : ""}`,
        restartNeeded: false,
      });
      setFolders(await bridge.leftoversFind(name));
      setPicked(new Set());
    } catch (reason) {
      setResult((reason as Error).message);
    }
  }

  if (!folders || (folders.length === 0 && !result)) {
    return null;
  }

  return (
    <div aria-label={`Leftovers from ${name}`} role="region" className="mb-3 rounded-md border border-border bg-card p-4">
      <p className="font-semibold">{name} left these folders behind</p>
      <p className="text-sm text-muted-foreground">They often hold settings you don't need anymore. Folders outside your own profile can only be shown.</p>
      <ul className="mt-2 flex flex-col gap-1">
        {folders.map((folder) => (
          <li key={folder.path} className="flex items-center gap-3 text-sm">
            {folder.canRemove ? (
              <input
                type="checkbox"
                aria-label={`Remove ${folder.path}`}
                checked={picked.has(folder.path)}
                onChange={() =>
                  setPicked((current) => {
                    const next = new Set(current);
                    if (!next.delete(folder.path)) {
                      next.add(folder.path);
                    }
                    return next;
                  })
                }
              />
            ) : (
              <span className="w-10 shrink-0" />
            )}
            <span className="min-w-0 flex-1 break-all">{folder.path}</span>
            <span className="shrink-0 tabular-nums text-muted-foreground">{formatBytes(folder.bytes)}</span>
            <button
              type="button"
              aria-label={`Show ${folder.path} in File Explorer`}
              title="Show in File Explorer"
              onClick={() => bridge.spaceShow(folder.path).catch(() => undefined)}
              className="grid size-9 shrink-0 cursor-pointer place-items-center rounded-md hover:bg-muted"
            >
              <FolderOpen aria-hidden="true" className="size-4" />
            </button>
          </li>
        ))}
      </ul>
      {picked.size > 0 && (
        <button type="button" onClick={recycle} className={`${buttonClass} mt-2`}>
          Move {picked.size} to the Recycle Bin
        </button>
      )}
      <Message result={result} />
    </div>
  );
}

/** The delay buttons for the startup list. Returns the set of delayed names and an action. */
export function useDelayedStartup() {
  const [delayed, setDelayed] = useState<DelayedStartup[]>([]);
  const reload = () => bridge.startupDelayed().then(setDelayed, () => setDelayed([]));
  useEffect(() => {
    void reload();
  }, []);
  return {
    isDelayed: (source: string, name: string) => delayed.some((item) => item.source === source && item.name === name),
    delayed,
    reload,
  };
}

export function BackgroundAppsCard() {
  const [report, setReport] = useState<BackgroundAppsReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState("");

  useEffect(() => {
    bridge.backgroundRead().then(setReport, (reason: Error) => setError(reason.message));
  }, []);

  async function set(id: string, allowed: boolean) {
    try {
      setReport(await bridge.backgroundSet(id, allowed));
    } catch (reason) {
      setError((reason as Error).message);
    }
  }

  const shown = (report?.apps ?? []).filter((app) => app.name.toLowerCase().includes(query.toLowerCase()));
  return (
    <section aria-labelledby="bg-title" className={`${cardClass} mt-5`}>
      <Header id="bg-title" title="Background apps" note="Store apps allowed to run when you're not using them. Turning one off saves memory and battery, and only affects your account." />
      {error && <p role="alert" className="mt-3 font-medium text-destructive">{error}</p>}
      {report?.allBlocked && (
        <p className="mt-3 font-medium text-success">Background apps are already off for every app, so there's nothing to change here.</p>
      )}
      {report && !report.allBlocked && (
        <>
          <input
            type="search"
            aria-label="Search background apps"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="Search"
            className="mt-3 min-h-11 w-full rounded-md border border-border bg-background px-3"
          />
          <div className="mt-3 flex max-h-96 flex-col gap-1 overflow-y-auto">
            {shown.map((app) => (
              <label key={app.id} className="flex min-h-11 cursor-pointer items-center justify-between gap-3 rounded-md px-2 hover:bg-muted">
                <span className="min-w-0 truncate" title={app.id}>
                  {app.name}
                </span>
                <input type="checkbox" checked={app.allowed} onChange={() => set(app.id, !app.allowed)} />
              </label>
            ))}
          </div>
        </>
      )}
    </section>
  );
}

/* ---------------- Dashboard and Privacy ---------------- */

export function HealthTrend() {
  const [points, setPoints] = useState<{ date: string; score: number }[] | null>(null);

  useEffect(() => {
    bridge.healthHistory().then(setPoints, () => setPoints([]));
  }, []);

  if (!points || points.length < 2) {
    return null;
  }

  const recent = points.slice(-30);
  const width = 600;
  const height = 110;
  // The scale starts a little under the lowest score, so small dips are easy to see.
  const floor = Math.max(0, Math.floor((Math.min(...recent.map((point) => point.score)) - 10) / 10) * 10);
  const x = (index: number) => 40 + (index / (recent.length - 1)) * (width - 52);
  const y = (score: number) => 10 + ((100 - score) / (100 - floor || 1)) * (height - 20);
  const line = recent.map((point, index) => `${index ? "L" : "M"}${x(index).toFixed(1)} ${y(point.score).toFixed(1)}`).join("");
  const first = recent[0];
  const last = recent[recent.length - 1];
  const change = last.score - first.score;
  const low = recent.reduce((worst, point) => (point.score < worst.score ? point : worst), first);

  return (
    <section aria-labelledby="trend-title" className={cardClass}>
      <h2 id="trend-title" className="text-lg font-semibold">
        Health over time
      </h2>
      <p className="mt-1 text-muted-foreground">
        {change === 0 ? "Steady" : change > 0 ? `Up ${change} points` : `Down ${-change} points`} since {day(first.date)}. Lowest: {low.score} on {day(low.date)}.
      </p>
      <svg viewBox={`0 0 ${width} ${height}`} className="mt-3 h-auto w-full" role="img" aria-label={`Health score from ${first.score} to ${last.score} over ${recent.length} days`}>
        {[100, floor].map((mark) => (
          <g key={mark}>
            <line x1={28} x2={width} y1={y(mark)} y2={y(mark)} strokeWidth={1} className="stroke-track" />
            <text x={0} y={y(mark) + 4} fontSize={11} className="fill-muted-foreground">
              {mark}
            </text>
          </g>
        ))}
        <path d={line} fill="none" strokeWidth={2} strokeLinejoin="round" className="stroke-primary" vectorEffect="non-scaling-stroke" />
        {recent.map((point, index) => (
          <g key={point.date}>
            <circle cx={x(index)} cy={y(point.score)} r={10} fill="transparent">
              <title>
                {day(point.date)}: {point.score}
              </title>
            </circle>
            <circle cx={x(index)} cy={y(point.score)} r={3} className="fill-primary" pointerEvents="none" />
          </g>
        ))}
      </svg>
    </section>
  );
}

export function ChangesCard() {
  const [report, setReport] = useState<ChangesReport | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    bridge.changesRead().then(setReport, (reason: Error) => setError(reason.message));
  }, []);

  const changed = report?.changes.filter((change) => change.added.length + change.removed.length > 0) ?? [];
  return (
    <section aria-labelledby="changes-title" className={cardClass}>
      <h2 id="changes-title" className="text-lg font-semibold">
        What changed
      </h2>
      {error && <p className="mt-1 font-medium text-destructive">{error}</p>}
      {report && !report.sinceDate && (
        <p className="mt-1 text-muted-foreground">TuneForge noted what's on your PC today. Come back in a few days to see what changed since.</p>
      )}
      {report?.sinceDate && (
        <p className="mt-1 text-muted-foreground">
          {changed.length === 0 ? `Nothing new since ${day(report.sinceDate)}.` : `New and removed since ${day(report.sinceDate)}.`}
        </p>
      )}
      {changed.length > 0 && (
        <dl className="mt-3 flex flex-col gap-3">
          {changed.map((change) => (
            <div key={change.category}>
              <dt className="font-semibold">{change.category}</dt>
              <dd className="mt-1 text-sm">
                {change.added.map((item) => (
                  <p key={`+${item}`}>
                    <span className="text-success">Added</span> {item}
                  </p>
                ))}
                {change.removed.map((item) => (
                  <p key={`-${item}`}>
                    <span className="text-muted-foreground">Removed</span> {item}
                  </p>
                ))}
              </dd>
            </div>
          ))}
        </dl>
      )}
    </section>
  );
}

const capabilityNames = { camera: "Camera", microphone: "Microphone", location: "Location" };
const capabilityPages = { camera: "ms-settings:privacy-webcam", microphone: "ms-settings:privacy-microphone", location: "ms-settings:privacy-location" };

export function PermissionsCard() {
  const [uses, setUses] = useState<PermissionUse[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    bridge.permissionsRead().then(setUses, (reason: Error) => setError(reason.message));
  }, []);

  return (
    <section aria-labelledby="perm-title" className={`${cardClass} mb-5`}>
      <Header id="perm-title" title="Camera, microphone and location" note="Which apps used them most recently, from Windows' own record. To block an app, open the Windows setting." />
      {error && <p role="alert" className="mt-3 font-medium text-destructive">{error}</p>}
      {uses?.length === 0 && <p className="mt-3 text-muted-foreground">Windows hasn't recorded any app using them.</p>}
      {uses && uses.length > 0 && (
        <div className="mt-3 grid gap-4 lg:grid-cols-3">
          {(["camera", "microphone", "location"] as const).map((capability) => {
            const list = uses.filter((use) => use.capability === capability).slice(0, 8);
            return (
              <div key={capability}>
                <h3 className="font-semibold">{capabilityNames[capability]}</h3>
                {list.length === 0 && <p className="text-sm text-muted-foreground">No app has used it.</p>}
                <ul className="mt-1 flex flex-col gap-1 text-sm">
                  {list.map((use) => (
                    <li key={use.app} className="flex justify-between gap-3">
                      <span className="min-w-0 truncate">{use.app}</span>
                      <span className={`shrink-0 ${use.inUse ? "font-semibold text-warning" : "text-muted-foreground"}`}>{use.inUse ? "In use now" : day(use.lastUsedUtc)}</span>
                    </li>
                  ))}
                </ul>
                <button type="button" onClick={() => bridge.settingsPage(capabilityPages[capability]).catch(() => undefined)} className={`${buttonClass} mt-2`}>
                  Open {capabilityNames[capability].toLowerCase()} settings
                </button>
              </div>
            );
          })}
        </div>
      )}
    </section>
  );
}
