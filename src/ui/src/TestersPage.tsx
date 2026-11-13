import { useEffect, useRef, useState, type KeyboardEvent, type MouseEvent } from "react";
import { bridge, type BatteryReport } from "./bridge";
import { ControllerTest } from "./ControllerTest";
import { HeadsetTest, WebcamTest } from "./ExtraPanels";

type Tab = "keyboard" | "mouse" | "display" | "controller" | "headset" | "webcam" | "battery";

const tabs: { id: Tab; label: string }[] = [
  { id: "keyboard", label: "Keyboard" },
  { id: "mouse", label: "Mouse" },
  { id: "display", label: "Display" },
  { id: "controller", label: "Controller" },
  { id: "headset", label: "Headset" },
  { id: "webcam", label: "Webcam" },
  { id: "battery", label: "Battery" },
];

const sectionClass = "rounded-lg border border-border bg-card p-6";
const buttonClass = "min-h-11 cursor-pointer rounded-md border border-border px-4 font-medium disabled:cursor-wait disabled:opacity-60";

export function TestersPage({ initialTab }: { initialTab?: string }) {
  const [tab, setTab] = useState<Tab>(tabs.some((item) => item.id === initialTab) ? (initialTab as Tab) : "keyboard");

  return (
    <div>
      <div role="tablist" aria-label="Testers" className="flex flex-wrap gap-2">
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
        {tab === "keyboard" && <KeyboardTest />}
        {tab === "mouse" && <MouseTest />}
        {tab === "display" && <DisplayTest />}
        {tab === "controller" && <ControllerTest />}
        {tab === "headset" && <HeadsetTest />}
        {tab === "webcam" && <WebcamTest />}
        {tab === "battery" && <BatteryCard />}
      </div>
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
  const [most, setMost] = useState(0);

  function press(event: KeyboardEvent) {
    event.preventDefault();
    setSeen((current) => new Set(current).add(event.code));
    setDown((current) => {
      const next = new Set(current).add(event.code);
      setMost((best) => Math.max(best, next.size));
      return next;
    });
  }

  function release(event: KeyboardEvent) {
    event.preventDefault();
    setDown((current) => {
      const next = new Set(current);
      next.delete(event.code);
      return next;
    });
  }

  function reset() {
    setSeen(new Set());
    setMost(0);
  }

  const tested = keyRows.flat().filter(([code]) => seen.has(code)).length;
  return (
    <section aria-labelledby="keyboard-title" className={sectionClass}>
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
      <div className="mt-4 rounded-md border border-border p-4">
        <h3 className="font-semibold">Ghosting</h3>
        <p className="mt-1 text-sm text-muted-foreground">
          Hold down as many keys as you can at once, like W, A, S, D, Shift and Space together. A key that doesn't light up while
          others are held is one your keyboard can't send alongside them.
        </p>
        <p className="mt-2 tabular-nums">
          Held now: <b>{down.size}</b> · Most at once: <b data-testid="most-keys">{most}</b>
        </p>
      </div>
      <button type="button" onClick={reset} className={`${buttonClass} mt-3`}>
        Reset
      </button>
    </section>
  );
}

const mouseButtons = ["Left", "Middle", "Right", "Back", "Forward"];

/** A switch that bounces sends a second click faster than any finger can. */
export const CHATTER_MS = 60;

function MouseTest() {
  const [clicks, setClicks] = useState<number[]>([0, 0, 0, 0, 0]);
  const [chatter, setChatter] = useState<number[]>([0, 0, 0, 0, 0]);
  const [rate, setRate] = useState(0);
  const [best, setBest] = useState(0);
  const pad = useRef<HTMLDivElement>(null);
  const moves = useRef(0);
  const released = useRef<number[]>([0, 0, 0, 0, 0]);

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

  // Mouse events, not pointer events: a pointer only reports the first of several buttons held together.
  function press(event: MouseEvent) {
    event.preventDefault();
    const button = event.button;
    if (button > 4) {
      return;
    }
    const gap = event.timeStamp - released.current[button];
    if (released.current[button] > 0 && gap >= 0 && gap < CHATTER_MS) {
      setChatter((current) => current.map((value, index) => (index === button ? value + 1 : value)));
    }
    setClicks((current) => current.map((value, index) => (index === button ? value + 1 : value)));
  }

  function lift(event: MouseEvent) {
    if (event.button <= 4) {
      released.current[event.button] = event.timeStamp;
    }
  }

  const faults = chatter.reduce((sum, value) => sum + value, 0);
  return (
    <section aria-labelledby="mouse-title" className={sectionClass}>
      <h2 id="mouse-title" className="text-lg font-semibold">
        Mouse
      </h2>
      <p className="mt-1 text-muted-foreground">
        Click every button here a few times at a normal pace, then move the mouse quickly in circles.
      </p>
      <div
        ref={pad}
        data-testid="mouse-pad"
        onMouseDown={press}
        onMouseUp={lift}
        onAuxClick={(event) => event.preventDefault()}
        onContextMenu={(event) => event.preventDefault()}
        className="mt-3 h-32 cursor-crosshair rounded-md border border-border bg-muted"
      />
      <dl className="mt-3 grid grid-cols-[auto_1fr] gap-x-6 gap-y-1">
        {mouseButtons.map((name, index) => (
          <div key={name} className="contents">
            <dt className="text-muted-foreground">{name}</dt>
            <dd className="tabular-nums">
              {clicks[index]} clicks
              {chatter[index] > 0 && <span className="text-warning">, {chatter[index]} accidental double clicks</span>}
            </dd>
          </div>
        ))}
        <dt className="text-muted-foreground">Polling rate</dt>
        <dd className="tabular-nums">
          {rate} Hz now, {best} Hz best
        </dd>
      </dl>
      <p role="status" className={`mt-3 text-sm ${faults > 0 ? "text-warning" : "text-muted-foreground"}`}>
        {faults > 0
          ? "A button sent two clicks closer together than a finger can. That's usually a worn switch."
          : `Double clicks under ${CHATTER_MS} ms apart are counted as accidental. None so far.`}
      </p>
    </section>
  );
}

const testColors = [
  { name: "Black", value: "#000000", tip: "Dim the room. Light leaking in at the edges is backlight bleed. Dots of color are stuck pixels." },
  { name: "White", value: "#ffffff", tip: "Look for dark dots. Those are dead pixels." },
  { name: "Red", value: "#ff0000", tip: "Look for dots that aren't red." },
  { name: "Green", value: "#00ff00", tip: "Look for dots that aren't green." },
  { name: "Blue", value: "#0000ff", tip: "Look for dots that aren't blue." },
  { name: "Gray", value: "#808080", tip: "Look for patches that are lighter or darker than the rest." },
];

function DisplayTest() {
  const [index, setIndex] = useState<number | null>(null);
  const [showTip, setShowTip] = useState(false);
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

  // Each color's tip shows briefly, then gets out of the way of the pixels.
  useEffect(() => {
    if (index === null) {
      return undefined;
    }
    setShowTip(true);
    const timer = setTimeout(() => setShowTip(false), 2500);
    return () => clearTimeout(timer);
  }, [index]);

  async function start() {
    setIndex(0);
    await screenRef.current?.requestFullscreen?.();
    screenRef.current?.focus();
  }

  function move(step: number) {
    if (index === null) {
      return;
    }

    const next = index + step;
    if (next < 0) {
      return;
    }
    if (next < testColors.length) {
      setIndex(next);
    } else {
      void document.exitFullscreen?.();
      setIndex(null);
    }
  }

  const color = index === null ? null : testColors[index];
  return (
    <section aria-labelledby="display-title" className={sectionClass}>
      <h2 id="display-title" className="text-lg font-semibold">
        Display
      </h2>
      <p className="mt-1 text-muted-foreground">
        Fills the screen with solid colors, so dead pixels, stuck pixels and backlight bleed stand out. Click or press a key for the
        next color, Left arrow to go back. Esc exits.
      </p>
      <button type="button" onClick={start} className="mt-3 min-h-11 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary">
        Start display test
      </button>
      <div
        ref={screenRef}
        tabIndex={-1}
        role="button"
        aria-label={color ? `${color.name} test screen, click for next` : undefined}
        hidden={color === null}
        onClick={() => move(1)}
        onKeyDown={(event) => {
          if (event.key === "ArrowLeft") {
            move(-1);
          } else if (event.key !== "Escape") {
            move(1);
          }
        }}
        className="relative h-full w-full cursor-none"
        style={{ background: color?.value }}
      >
        {color && (
          <p
            className={`absolute bottom-8 left-1/2 max-w-[40rem] -translate-x-1/2 rounded-md bg-black/70 px-4 py-2 text-center text-white transition-opacity duration-500 ${
              showTip ? "opacity-100" : "opacity-0"
            }`}
          >
            {color.name} ({index! + 1} of {testColors.length}). {color.tip}
          </p>
        )}
      </div>
    </section>
  );
}

function BatteryCard() {
  const [report, setReport] = useState<BatteryReport | null>(null);
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState<string | null>(null);

  async function check() {
    setBusy(true);
    setError(null);
    try {
      setReport(await bridge.batteryRead());
    } catch (reason) {
      setError((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  useEffect(() => {
    void check();
  }, []);

  return (
    <section aria-labelledby="battery-title" className={sectionClass}>
      <h2 id="battery-title" className="text-lg font-semibold">
        Battery health
      </h2>
      <p className="mt-1 text-muted-foreground">How much charge your battery still holds compared with when it was new, from Windows' own battery report.</p>
      <div role="status">
        {busy && <p className="mt-3 text-muted-foreground">Reading the battery report.</p>}
        {error && <p className="mt-3 font-medium text-destructive">{error}</p>}
        {report && !busy && report.batteries.length === 0 && <p className="mt-3 text-muted-foreground">This PC has no battery, so there's nothing to check.</p>}
      </div>
      {!busy &&
        report?.batteries.map((battery, index) => {
          const health = battery.designMwh > 0 ? Math.round((battery.fullMwh / battery.designMwh) * 100) : null;
          return (
            <div key={`${battery.name}${index}`} className="mt-4">
              <p className="text-sm text-muted-foreground">{[battery.maker, battery.name].filter(Boolean).join(" ") || "Battery"}</p>
              {health !== null && (
                <>
                  <p className={`text-3xl font-semibold tabular-nums ${health >= 80 ? "text-success" : health >= 60 ? "text-warning" : "text-destructive"}`}>{health}%</p>
                  <div role="meter" aria-label="Battery health" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.min(health, 100)} className="mt-2 h-2 max-w-md overflow-hidden rounded-full bg-track">
                    <div className="h-full bg-primary" style={{ width: `${Math.min(health, 100)}%` }} />
                  </div>
                  <p className="mt-2 text-sm">
                    {health >= 80
                      ? "Healthy. It holds most of its original charge."
                      : health >= 60
                        ? "Worn. Expect noticeably shorter battery life than when it was new."
                        : "Heavily worn. A replacement would bring back most of your battery life."}
                  </p>
                </>
              )}
              <dl className="mt-3 grid grid-cols-[auto_1fr] gap-x-6 gap-y-1 text-sm">
                <dt className="text-muted-foreground">Holds now</dt>
                <dd className="tabular-nums">{(battery.fullMwh / 1000).toFixed(1)} Wh</dd>
                <dt className="text-muted-foreground">When new</dt>
                <dd className="tabular-nums">{(battery.designMwh / 1000).toFixed(1)} Wh</dd>
                {battery.cycles !== null && (
                  <>
                    <dt className="text-muted-foreground">Charge cycles</dt>
                    <dd className="tabular-nums">{battery.cycles}</dd>
                  </>
                )}
              </dl>
            </div>
          );
        })}
      <button type="button" disabled={busy} aria-busy={busy} onClick={check} className={`${buttonClass} mt-4`}>
        {busy ? "Checking" : "Check again"}
      </button>
    </section>
  );
}
