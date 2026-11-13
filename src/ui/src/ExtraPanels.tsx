import { useCallback, useEffect, useRef, useState } from "react";
import { bridge, type BandwidthRule, type GpuHealth, type OperationResult, type ProbeSummary, type SpeedResult } from "./bridge";

const buttonClass =
  "min-h-11 shrink-0 cursor-pointer rounded-md border border-border px-4 font-medium disabled:cursor-not-allowed disabled:opacity-60";
const primaryClass =
  "min-h-11 shrink-0 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary disabled:cursor-not-allowed disabled:opacity-60";
const sectionClass = "rounded-lg border border-border bg-card p-6";
const fieldClass = "min-h-11 rounded-md border border-border bg-background px-3";

function Outcome({ result }: { result: OperationResult | string | null }) {
  const text = typeof result === "string" ? result : result?.message;
  const failed = typeof result === "string" || result?.ok === false;
  return <div role="status">{text && <p className={`mt-2 font-medium ${failed ? "text-destructive" : "text-success"}`}>{text}</p>}</div>;
}

export function SpeedCard() {
  const [running, setRunning] = useState(false);
  const [result, setResult] = useState<SpeedResult | null>(null);

  async function run() {
    setRunning(true);
    setResult(null);
    try {
      setResult(await bridge.speedRun());
    } catch (reason) {
      setResult({ error: (reason as Error).message });
    } finally {
      setRunning(false);
    }
  }

  return (
    <section aria-labelledby="speed-title" className={sectionClass}>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="min-w-0">
          <h2 id="speed-title" className="text-lg font-semibold">
            Bandwidth test
          </h2>
          <p className="text-muted-foreground">Downloads 25 MB and uploads 5 MB through Cloudflare's speed test.</p>
        </div>
        <button type="button" disabled={running} aria-busy={running} onClick={run} className={primaryClass}>
          {running ? "Testing" : "Run test"}
        </button>
      </div>
      <div role="status">
        {result?.error && <p className="mt-2 font-medium text-destructive">{result.error}</p>}
        {result?.downloadMbps != null && (
          <p className="mt-2 text-lg font-semibold tabular-nums">
            {result.downloadMbps.toFixed(0)} Mbps down, {result.uploadMbps?.toFixed(0)} Mbps up
          </p>
        )}
      </div>
    </section>
  );
}

/** Repeats a ping, for watching a game server over time. */
export function PingMonitor() {
  const [host, setHost] = useState("");
  const [running, setRunning] = useState(false);
  const [samples, setSamples] = useState<ProbeSummary[]>([]);
  const [error, setError] = useState<string | null>(null);
  const live = useRef(false);

  useEffect(() => () => {
    live.current = false;
  }, []);

  async function start() {
    live.current = true;
    setRunning(true);
    setError(null);
    setSamples([]);
    while (live.current) {
      try {
        const next = await bridge.networkPing(host);
        if (live.current) {
          setSamples((current) => [next, ...current].slice(0, 30));
        }
      } catch (reason) {
        setError((reason as Error).message);
        live.current = false;
      }
    }
    setRunning(false);
  }

  const latest = samples[0];
  return (
    <section aria-labelledby="monitor-title" className={sectionClass}>
      <h2 id="monitor-title" className="text-lg font-semibold">
        Ping monitor
      </h2>
      <p className="text-muted-foreground">Enter a game server's address and leave this running. Each row is a 10-ping sample, newest first.</p>
      <div className="mt-3 flex flex-wrap gap-2">
        <input
          aria-label="Server to watch"
          value={host}
          onChange={(event) => setHost(event.target.value)}
          placeholder="Server address"
          className={`${fieldClass} min-w-0 flex-1`}
        />
        <button
          type="button"
          disabled={!running && host.trim() === ""}
          onClick={() => {
            if (running) {
              live.current = false;
            } else {
              void start();
            }
          }}
          className={primaryClass}
        >
          {running ? "Stop watching" : "Start watching"}
        </button>
      </div>
      {error && <p role="alert" className="mt-2 font-medium text-destructive">{error}</p>}
      {latest && (
        <ul className="mt-3 flex flex-col gap-1 tabular-nums">
          {samples.map((sample, index) => (
            <li key={samples.length - index} className="flex justify-between gap-3 text-sm">
              <span>{sample.avgMs == null ? "No answer" : `${sample.avgMs.toFixed(1)} ms average`}</span>
              <span className="text-muted-foreground">
                {sample.jitterMs == null ? "" : `${sample.jitterMs.toFixed(1)} ms jitter, `}
                {Math.round(sample.lossPercent)}% loss
              </span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

export function BandwidthCard() {
  const [rules, setRules] = useState<BandwidthRule[] | null>(null);
  const [program, setProgram] = useState("");
  const [mbps, setMbps] = useState("10");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<OperationResult | string | null>(null);

  const reload = useCallback(() => {
    bridge.bandwidthList().then(setRules, () => setRules([]));
  }, []);
  useEffect(reload, [reload]);

  async function run(work: () => Promise<OperationResult>) {
    setBusy(true);
    try {
      setResult(await work());
      reload();
    } catch (reason) {
      setResult((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby="limit-title" className={sectionClass}>
      <h2 id="limit-title" className="text-lg font-semibold">
        Upload limits
      </h2>
      <p className="text-muted-foreground">
        Caps how fast one program can upload, so a backup or sync doesn't lag your games. Windows can only limit uploads this way, not downloads.
      </p>
      <div className="mt-3 flex flex-wrap gap-2">
        <input
          aria-label="Program file name"
          value={program}
          onChange={(event) => setProgram(event.target.value)}
          placeholder="program.exe"
          className={`${fieldClass} min-w-0 flex-1`}
        />
        <input
          aria-label="Limit in Mbps"
          type="number"
          min={1}
          value={mbps}
          onChange={(event) => setMbps(event.target.value)}
          className={`${fieldClass} w-24`}
        />
        <button type="button" disabled={busy || program.trim() === ""} onClick={() => run(() => bridge.bandwidthSet(program, Number(mbps)))} className={buttonClass}>
          Set limit
        </button>
      </div>
      <Outcome result={result} />
      {rules?.map((rule) => (
        <div key={rule.program} className="mt-2 flex items-center justify-between gap-3">
          <span className="min-w-0 break-all">
            {rule.program}, {rule.mbps} Mbps
          </span>
          <button type="button" disabled={busy} aria-label={`Remove limit on ${rule.program}`} onClick={() => run(() => bridge.bandwidthRemove(rule.program))} className={buttonClass}>
            Remove
          </button>
        </div>
      ))}
    </section>
  );
}

export function TurboCard() {
  const [on, setOn] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    bridge.turboGet().then(setOn, () => setOn(false));
  }, []);

  async function change() {
    setBusy(true);
    try {
      setOn(await bridge.turboSet(!on));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby="turbo-title" className={`${sectionClass} mb-4 flex items-center justify-between gap-3`}>
      <div className="min-w-0">
        <h2 id="turbo-title" className="font-semibold">
          Turbo mode {on ? "is on" : ""}
        </h2>
        <p className="text-sm text-muted-foreground">
          Switches to High performance, silences notifications and closes your focus apps until you turn it off. Handy for games TuneForge didn't find.
        </p>
      </div>
      <button type="button" disabled={busy} onClick={change} className={primaryClass}>
        {on ? "Turn off" : "Turn on"}
      </button>
    </section>
  );
}

export function GpuCard() {
  const [gpu, setGpu] = useState<GpuHealth | null | undefined>(undefined);

  useEffect(() => {
    bridge.gpuHealth().then(setGpu, () => setGpu(null));
  }, []);

  if (gpu === undefined) {
    return null;
  }

  if (gpu === null) {
    return (
      <section aria-labelledby="gpu-title" className={sectionClass}>
        <h2 id="gpu-title" className="text-lg font-semibold">
          Graphics card health
        </h2>
        <p className="mt-1 text-muted-foreground">TuneForge can only read this on NVIDIA graphics cards.</p>
      </section>
    );
  }

  const hot = (gpu.temperatureC ?? 0) >= 85;
  return (
    <section aria-labelledby="gpu-title" className={sectionClass}>
      <h2 id="gpu-title" className="text-lg font-semibold">
        Graphics card health
      </h2>
      <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-6 gap-y-1 tabular-nums">
        <dt className="text-muted-foreground">Card</dt>
        <dd>{gpu.name}</dd>
        <dt className="text-muted-foreground">Driver</dt>
        <dd>{gpu.driver}</dd>
        <dt className="text-muted-foreground">Temperature</dt>
        <dd className={hot ? "font-medium text-warning" : ""}>
          {gpu.temperatureC == null ? "Not reported" : `${gpu.temperatureC} °C${hot ? ", running hot. Check its fans and dust." : ""}`}
        </dd>
        <dt className="text-muted-foreground">Fan</dt>
        <dd>{gpu.fanPercent == null ? "Not reported" : `${gpu.fanPercent}%`}</dd>
        <dt className="text-muted-foreground">Power</dt>
        <dd>{gpu.powerWatts == null ? "Not reported" : `${gpu.powerWatts.toFixed(0)} W`}</dd>
      </dl>
    </section>
  );
}

const biosSteps = [
  "Save your work first. Your PC restarts into its setup screen 30 seconds after you confirm.",
  "Menus differ by brand, so look for words like Advanced, PCI or Boot.",
  "Resize BAR is often listed as Re-Size BAR or Smart Access Memory, and may need Above 4G Decoding on too.",
  "XMP, EXPO or DOCP runs your memory at its rated speed.",
  "Change one thing at a time. If your PC won't start, use Load Defaults in the setup screen.",
];

export function BiosCard() {
  const [confirming, setConfirming] = useState(false);
  const [pending, setPending] = useState(false);
  const [result, setResult] = useState<OperationResult | string | null>(null);

  async function run(work: () => Promise<OperationResult>, isRestart: boolean) {
    setConfirming(false);
    try {
      const outcome = await work();
      setResult(outcome);
      setPending(isRestart && outcome.ok);
    } catch (reason) {
      setResult((reason as Error).message);
    }
  }

  return (
    <section aria-labelledby="bios-title" className={sectionClass}>
      <h2 id="bios-title" className="text-lg font-semibold">
        BIOS guide
      </h2>
      <ol className="mt-2 ml-5 flex list-decimal flex-col gap-1 text-muted-foreground">
        {biosSteps.map((step) => (
          <li key={step}>{step}</li>
        ))}
      </ol>
      <div className="mt-3 flex flex-wrap items-center gap-2">
        {pending ? (
          <button type="button" onClick={() => run(bridge.biosCancel, false)} className={buttonClass}>
            Cancel restart
          </button>
        ) : confirming ? (
          <>
            <span className="font-medium">Restart into BIOS now?</span>
            <button type="button" onClick={() => run(bridge.biosRestart, true)} className={primaryClass}>
              Yes, restart
            </button>
            <button type="button" onClick={() => setConfirming(false)} className={buttonClass}>
              Cancel
            </button>
          </>
        ) : (
          <button type="button" onClick={() => setConfirming(true)} className={buttonClass}>
            Restart into BIOS
          </button>
        )}
      </div>
      <Outcome result={result} />
    </section>
  );
}

export function RegistryBackupCard() {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<OperationResult | string | null>(null);

  async function run() {
    setBusy(true);
    try {
      setResult(await bridge.registryBackup());
    } catch (reason) {
      setResult((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-labelledby="backup-reg-title" className="mt-6 rounded-lg border border-border bg-card p-4">
      <div className="flex items-center justify-between gap-3">
        <div className="min-w-0">
          <h2 id="backup-reg-title" className="font-semibold">
            Back up the registry
          </h2>
          <p className="text-muted-foreground">
            Saves your settings to Documents\TuneForge Backups and keeps the last 5 copies. It can take a minute. Settings can also do this weekly.
          </p>
        </div>
        <button type="button" disabled={busy} aria-busy={busy} onClick={run} className={buttonClass}>
          {busy ? "Working" : "Back up"}
        </button>
      </div>
      <Outcome result={result} />
    </section>
  );
}

function stopTracks(stream: MediaStream | null) {
  stream?.getTracks().forEach((track) => track.stop());
}

export function WebcamTest() {
  const video = useRef<HTMLVideoElement>(null);
  const stream = useRef<MediaStream | null>(null);
  const [on, setOn] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => () => stopTracks(stream.current), []);

  async function toggle() {
    setError(null);
    if (on) {
      stopTracks(stream.current);
      stream.current = null;
      setOn(false);
      return;
    }

    try {
      stream.current = await navigator.mediaDevices.getUserMedia({ video: true });
      if (video.current) {
        video.current.srcObject = stream.current;
      }
      setOn(true);
    } catch {
      setError("No camera was found, or Windows has blocked camera access.");
    }
  }

  return (
    <section aria-labelledby="webcam-title" className={sectionClass}>
      <h2 id="webcam-title" className="text-lg font-semibold">
        Webcam
      </h2>
      <p className="mt-1 text-muted-foreground">Shows your camera here. Nothing is recorded or saved.</p>
      <button type="button" onClick={toggle} className={`${buttonClass} mt-3`}>
        {on ? "Stop camera" : "Start camera"}
      </button>
      {error && <p role="alert" className="mt-2 font-medium text-destructive">{error}</p>}
      <video ref={video} autoPlay muted playsInline hidden={!on} className="mt-3 max-h-64 rounded-md" />
    </section>
  );
}

export function HeadsetTest() {
  const [level, setLevel] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const stop = useRef<(() => void) | null>(null);

  useEffect(() => () => stop.current?.(), []);

  function tone(pan: number) {
    const audio = new AudioContext();
    const oscillator = audio.createOscillator();
    const gain = audio.createGain();
    const panner = audio.createStereoPanner();
    panner.pan.value = pan;
    gain.gain.value = 0.15;
    oscillator.connect(gain).connect(panner).connect(audio.destination);
    oscillator.start();
    oscillator.stop(audio.currentTime + 0.6);
    oscillator.onended = () => void audio.close();
  }

  async function microphone() {
    setError(null);
    if (stop.current) {
      stop.current();
      stop.current = null;
      setLevel(null);
      return;
    }

    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      const audio = new AudioContext();
      const analyser = audio.createAnalyser();
      audio.createMediaStreamSource(stream).connect(analyser);
      const data = new Uint8Array(analyser.fftSize);
      const timer = setInterval(() => {
        analyser.getByteTimeDomainData(data);
        setLevel(Math.min(100, (Math.max(...data) - 128) * 2));
      }, 100);
      stop.current = () => {
        clearInterval(timer);
        stopTracks(stream);
        void audio.close();
      };
    } catch {
      setError("No microphone was found, or Windows has blocked microphone access.");
    }
  }

  return (
    <section aria-labelledby="headset-title" className={sectionClass}>
      <h2 id="headset-title" className="text-lg font-semibold">
        Headset
      </h2>
      <p className="mt-1 text-muted-foreground">Play a tone on each side, then speak to check your microphone. Nothing is recorded.</p>
      <div className="mt-3 flex flex-wrap gap-2">
        <button type="button" onClick={() => tone(-1)} className={buttonClass}>
          Left
        </button>
        <button type="button" onClick={() => tone(1)} className={buttonClass}>
          Right
        </button>
        <button type="button" onClick={microphone} className={buttonClass}>
          {level === null ? "Test microphone" : "Stop microphone"}
        </button>
      </div>
      {error && <p role="alert" className="mt-2 font-medium text-destructive">{error}</p>}
      {level !== null && (
        <div role="meter" aria-label="Microphone level" aria-valuemin={0} aria-valuemax={100} aria-valuenow={level} className="mt-3 h-1 overflow-hidden rounded-full bg-track">
          <div className="h-full bg-primary" style={{ width: `${level}%` }} />
        </div>
      )}
    </section>
  );
}

