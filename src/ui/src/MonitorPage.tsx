import { useEffect, useRef, useState, type KeyboardEvent, type PointerEvent } from "react";
import { bridge, type ErrorGroup, type StorageReport, type WindowsEvent } from "./bridge";
import { ControllerTest, GpuCard, HeadsetTest, WebcamTest } from "./ExtraPanels";
import { formatBytes } from "./format";
import { DriverCard, StartupCard } from "./Insights";

type Tab = "storage" | "errors" | "logs" | "tests";

const tabs: { id: Tab; label: string }[] = [
  { id: "storage", label: "Disks" },
  { id: "errors", label: "Windows errors" },
  { id: "logs", label: "Logs" },
  { id: "tests", label: "Hardware tests" },
];

export function MonitorPage({ initialTab }: { initialTab?: string }) {
  const [tab, setTab] = useState<Tab>((initialTab as Tab) ?? "storage");

  return (
    <div>
      <div className="mb-5 grid gap-3 lg:grid-cols-2">
        <StartupCard />
        <DriverCard />
      </div>
      <div role="tablist" aria-label="Monitor sections" className="flex flex-wrap gap-2">
        {tabs.map((item) => (
          <button
            key={item.id}
            type="button"
            role="tab"
            aria-selected={tab === item.id}
            onClick={() => setTab(item.id)}
            className={`min-h-11 cursor-pointer rounded-md border border-border px-4 font-medium transition-colors duration-200 ${
              tab === item.id ? "bg-muted" : "bg-card text-muted-foreground hover:bg-muted"
            }`}
          >
            {item.label}
          </button>
        ))}
      </div>
      <div role="tabpanel" aria-label={tabs.find((item) => item.id === tab)?.label} className="mt-4">
        {tab === "storage" && <StorageTab />}
        {tab === "errors" && <ErrorsTab />}
        {tab === "logs" && <LogsTab />}
        {tab === "tests" && <HardwareTests />}
      </div>
    </div>
  );
}

const healthLabels = { healthy: "Healthy", warning: "Warning", unhealthy: "Failing", unknown: "Unknown" };
const healthColors = {
  healthy: "text-success",
  warning: "text-warning",
  unhealthy: "text-destructive",
  unknown: "text-muted-foreground",
};

function StorageTab() {
  const [report, setReport] = useState<StorageReport | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    bridge.storageRead().then(setReport, (reason: Error) => setError(reason.message));
  }, []);

  if (error) {
    return <p role="alert" className="font-medium text-destructive">{error}</p>;
  }
  if (!report) {
    return <p className="text-muted-foreground">Reading disks.</p>;
  }

  return (
    <div className="flex flex-col gap-2">
      <GpuCard />
      {report.disks.map((disk) => (
        <div key={disk.name} className="flex items-center justify-between gap-3 rounded-md border border-border bg-card p-3">
          <div className="min-w-0">
            <p className="font-medium">{disk.name}</p>
            <p className="text-sm text-muted-foreground">
              {disk.kind}, {formatBytes(disk.sizeBytes)}
            </p>
          </div>
          <span className={`shrink-0 font-medium ${healthColors[disk.health]}`}>{healthLabels[disk.health]}</span>
        </div>
      ))}
      {report.volumes.map((volume) => {
        const used = volume.totalBytes ? 100 * (1 - volume.freeBytes / volume.totalBytes) : 0;
        return (
          <div key={volume.name} className="rounded-md border border-border bg-card p-3">
            <div className="flex justify-between gap-3">
              <p className="font-medium">
                {volume.name} {volume.label}
              </p>
              <p className="shrink-0 text-sm text-muted-foreground">
                {formatBytes(volume.freeBytes)} free of {formatBytes(volume.totalBytes)}
              </p>
            </div>
            <div
              role="meter"
              aria-label={`${volume.name} space used`}
              aria-valuemin={0}
              aria-valuemax={100}
              aria-valuenow={Math.round(used)}
              className="mt-2 h-2 overflow-hidden rounded-full bg-muted"
            >
              <div className="h-full rounded-full bg-primary" style={{ width: `${used}%` }} />
            </div>
          </div>
        );
      })}
    </div>
  );
}

function when(utc: string) {
  return new Date(utc).toLocaleString();
}

function ErrorsTab() {
  const [groups, setGroups] = useState<ErrorGroup[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    bridge.eventErrors().then(setGroups, (reason: Error) => setError(reason.message));
  }, []);

  if (error) {
    return <p role="alert" className="font-medium text-destructive">{error}</p>;
  }

  return (
    <div>
      <p className="mb-3 text-muted-foreground">Errors from the last 7 days, most frequent first. Many are harmless.</p>
      {!groups && <p className="text-muted-foreground">Reading Windows logs.</p>}
      {groups?.length === 0 && <p className="text-muted-foreground">No errors in the last 7 days.</p>}
      <div className="flex flex-col gap-2">
        {groups?.map((group) => (
          <div key={`${group.source}${group.eventId}`} className="rounded-md border border-border bg-card p-3">
            <div className="flex justify-between gap-3">
              <p className="min-w-0 font-medium break-words">{group.source}</p>
              <p className="shrink-0 text-sm text-muted-foreground">{group.count} times</p>
            </div>
            <p className="text-sm text-muted-foreground">
              Event {group.eventId}, last on {when(group.lastUtc)}
            </p>
            {group.message && <p className="mt-1 text-sm break-words">{group.message}</p>}
          </div>
        ))}
      </div>
    </div>
  );
}

function LogsTab() {
  const [log, setLog] = useState("System");
  const [level, setLevel] = useState("errors");
  const [events, setEvents] = useState<WindowsEvent[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setEvents(null);
    setError(null);
    bridge.eventRead(log, level).then(setEvents, (reason: Error) => setError(reason.message));
  }, [log, level]);

  const fieldClass = "min-h-11 cursor-pointer rounded-md border border-border bg-card px-3";
  return (
    <div>
      <div className="mb-3 flex flex-wrap gap-2">
        <select aria-label="Log" value={log} onChange={(event) => setLog(event.target.value)} className={fieldClass}>
          <option value="System">System</option>
          <option value="Application">Application</option>
        </select>
        <select aria-label="Level" value={level} onChange={(event) => setLevel(event.target.value)} className={fieldClass}>
          <option value="errors">Errors</option>
          <option value="warnings">Errors and warnings</option>
        </select>
      </div>
      {error && <p role="alert" className="font-medium text-destructive">{error}</p>}
      {!events && !error && <p className="text-muted-foreground">Reading the log.</p>}
      {events?.length === 0 && <p className="text-muted-foreground">Nothing in the last 30 days.</p>}
      <div className="flex flex-col gap-2">
        {events?.map((item, index) => (
          <div key={index} className="rounded-md border border-border bg-card p-3">
            <p className="font-medium break-words">{item.source}</p>
            <p className="text-sm text-muted-foreground">
              {item.level}, event {item.eventId}, {when(item.timeUtc)}
            </p>
            {item.message && <p className="mt-1 text-sm break-words">{item.message}</p>}
          </div>
        ))}
      </div>
    </div>
  );
}

function HardwareTests() {
  return (
    <div className="flex flex-col gap-6">
      <KeyboardTest />
      <MouseTest />
      <MonitorTest />
      <WebcamTest />
      <HeadsetTest />
      <ControllerTest />
    </div>
  );
}

const keyRows: [string, string][][] = [
  [["Escape", "Esc"], ["F1", "F1"], ["F2", "F2"], ["F3", "F3"], ["F4", "F4"], ["F5", "F5"], ["F6", "F6"], ["F7", "F7"], ["F8", "F8"], ["F9", "F9"], ["F10", "F10"], ["F11", "F11"], ["F12", "F12"]],
  [["Backquote", "`"], ["Digit1", "1"], ["Digit2", "2"], ["Digit3", "3"], ["Digit4", "4"], ["Digit5", "5"], ["Digit6", "6"], ["Digit7", "7"], ["Digit8", "8"], ["Digit9", "9"], ["Digit0", "0"], ["Minus", "-"], ["Equal", "="], ["Backspace", "Back"]],
  [["Tab", "Tab"], ["KeyQ", "Q"], ["KeyW", "W"], ["KeyE", "E"], ["KeyR", "R"], ["KeyT", "T"], ["KeyY", "Y"], ["KeyU", "U"], ["KeyI", "I"], ["KeyO", "O"], ["KeyP", "P"], ["BracketLeft", "["], ["BracketRight", "]"], ["Backslash", "\\"]],
  [["CapsLock", "Caps"], ["KeyA", "A"], ["KeyS", "S"], ["KeyD", "D"], ["KeyF", "F"], ["KeyG", "G"], ["KeyH", "H"], ["KeyJ", "J"], ["KeyK", "K"], ["KeyL", "L"], ["Semicolon", ";"], ["Quote", "'"], ["Enter", "Enter"]],
  [["ShiftLeft", "Shift"], ["KeyZ", "Z"], ["KeyX", "X"], ["KeyC", "C"], ["KeyV", "V"], ["KeyB", "B"], ["KeyN", "N"], ["KeyM", "M"], ["Comma", ","], ["Period", "."], ["Slash", "/"], ["ShiftRight", "Shift"]],
  [["ControlLeft", "Ctrl"], ["AltLeft", "Alt"], ["Space", "Space"], ["AltRight", "Alt"], ["ControlRight", "Ctrl"], ["ArrowLeft", "Left"], ["ArrowUp", "Up"], ["ArrowDown", "Down"], ["ArrowRight", "Right"]],
];

const keyCount = keyRows.flat().length;

function KeyboardTest() {
  const [seen, setSeen] = useState<Set<string>>(new Set());
  const [down, setDown] = useState<Set<string>>(new Set());

  function press(event: KeyboardEvent) {
    event.preventDefault();
    setSeen((current) => new Set(current).add(event.code));
    setDown((current) => new Set(current).add(event.code));
  }

  function release(event: KeyboardEvent) {
    event.preventDefault();
    setDown((current) => {
      const next = new Set(current);
      next.delete(event.code);
      return next;
    });
  }

  const tested = keyRows.flat().filter(([code]) => seen.has(code)).length;
  return (
    <section aria-labelledby="keyboard-title" className="rounded-lg border border-border bg-card p-6">
      <h2 id="keyboard-title" className="text-lg font-semibold">
        Keyboard
      </h2>
      <p className="mt-1 text-muted-foreground">
        Click the keyboard below, then press each key. {tested} of {keyCount} keys work so far.
      </p>
      <div
        tabIndex={0}
        role="application"
        aria-label="Keyboard test area"
        onKeyDown={press}
        onKeyUp={release}
        onBlur={() => setDown(new Set())}
        className="mt-3 flex cursor-pointer flex-col gap-1 rounded-md border border-border p-3"
      >
        {keyRows.map((row, index) => (
          <div key={index} className="flex gap-1">
            {row.map(([code, label]) => (
              <span
                key={code}
                data-testid={`key-${code}`}
                data-tested={seen.has(code)}
                className={`flex h-9 min-w-0 flex-1 items-center justify-center rounded border border-border text-xs transition-colors duration-150 ${
                  down.has(code) ? "bg-primary text-on-primary" : seen.has(code) ? "bg-muted font-semibold" : ""
                } ${code === "Space" ? "flex-[5]" : ""}`}
              >
                {label}
              </span>
            ))}
          </div>
        ))}
      </div>
      <button
        type="button"
        onClick={() => setSeen(new Set())}
        className="mt-3 min-h-11 cursor-pointer rounded-md border border-border px-4 font-medium"
      >
        Reset
      </button>
    </section>
  );
}

const mouseButtons = ["Left", "Middle", "Right", "Back", "Forward"];

function MouseTest() {
  const [clicks, setClicks] = useState<number[]>([0, 0, 0, 0, 0]);
  const [rate, setRate] = useState(0);
  const [best, setBest] = useState(0);
  const pad = useRef<HTMLDivElement>(null);
  const moves = useRef(0);

  useEffect(() => {
    const element = pad.current;
    const count = () => {
      moves.current += 1;
    };

    // The raw event fires at the mouse's own rate. Plain moves are limited to the screen's.
    const eventName = "onpointerrawupdate" in window ? "pointerrawupdate" : "pointermove";
    element?.addEventListener(eventName, count);
    const timer = setInterval(() => {
      const perSecond = moves.current * 2;
      moves.current = 0;
      setRate(perSecond);
      setBest((current) => Math.max(current, perSecond));
    }, 500);

    return () => {
      element?.removeEventListener(eventName, count);
      clearInterval(timer);
    };
  }, []);

  function press(event: PointerEvent) {
    event.preventDefault();
    const button = event.button;
    setClicks((current) => current.map((value, index) => (index === button ? value + 1 : value)));
  }

  return (
    <section aria-labelledby="mouse-title" className="rounded-lg border border-border bg-card p-6">
      <h2 id="mouse-title" className="text-lg font-semibold">
        Mouse
      </h2>
      <p className="mt-1 text-muted-foreground">Click every button here, then move the mouse quickly in circles.</p>
      <div
        ref={pad}
        data-testid="mouse-pad"
        onPointerDown={press}
        onContextMenu={(event) => event.preventDefault()}
        className="mt-3 h-32 cursor-crosshair rounded-md border border-border bg-muted"
      />
      <dl className="mt-3 grid grid-cols-[auto_1fr] gap-x-6 gap-y-1">
        {mouseButtons.map((name, index) => (
          <div key={name} className="contents">
            <dt className="text-muted-foreground">{name}</dt>
            <dd className="tabular-nums">{clicks[index]} clicks</dd>
          </div>
        ))}
        <dt className="text-muted-foreground">Report rate</dt>
        <dd className="tabular-nums">
          {rate} Hz now, {best} Hz best
        </dd>
      </dl>
    </section>
  );
}

const testColors = [
  { name: "Black", value: "#000000" },
  { name: "White", value: "#ffffff" },
  { name: "Red", value: "#ff0000" },
  { name: "Green", value: "#00ff00" },
  { name: "Blue", value: "#0000ff" },
  { name: "Gray", value: "#808080" },
];

function MonitorTest() {
  const [index, setIndex] = useState<number | null>(null);
  const screenRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const left = () => {
      if (!document.fullscreenElement) {
        setIndex(null);
      }
    };
    document.addEventListener("fullscreenchange", left);
    return () => document.removeEventListener("fullscreenchange", left);
  }, []);

  async function start() {
    setIndex(0);
    await screenRef.current?.requestFullscreen?.();
    screenRef.current?.focus();
  }

  function next() {
    if (index === null) {
      return;
    }

    if (index + 1 < testColors.length) {
      setIndex(index + 1);
    } else {
      void document.exitFullscreen?.();
      setIndex(null);
    }
  }

  return (
    <section aria-labelledby="monitor-title" className="rounded-lg border border-border bg-card p-6">
      <h2 id="monitor-title" className="text-lg font-semibold">
        Monitor
      </h2>
      <p className="mt-1 text-muted-foreground">
        Fills the screen with solid colors, so dead or stuck pixels stand out. Click or press a key for the next color. Esc exits.
      </p>
      <button
        type="button"
        onClick={start}
        className="mt-3 min-h-11 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary"
      >
        Start monitor test
      </button>
      <div
        ref={screenRef}
        tabIndex={-1}
        role="button"
        aria-label={index === null ? undefined : `${testColors[index].name} test screen, click for next`}
        hidden={index === null}
        onClick={next}
        onKeyDown={(event) => {
          if (event.key !== "Escape") {
            next();
          }
        }}
        className="h-full w-full cursor-none"
        style={{ background: index === null ? undefined : testColors[index].value }}
      />
    </section>
  );
}
