import { useEffect, useRef, useState } from "react";

export type PadLayout = "xbox" | "playstation" | "nintendo" | "standard";

/** Windows names the controller, and the maker's USB number tells the layouts apart. */
export function detectLayout(id: string): PadLayout {
  const text = id.toLowerCase();
  if (/045e|xbox|xinput/.test(text)) {
    return "xbox";
  }
  if (/054c|dualsense|dualshock|playstation|wireless controller/.test(text)) {
    return "playstation";
  }
  if (/057e|nintendo|pro controller|joy-con/.test(text)) {
    return "nintendo";
  }
  return "standard";
}

export function padName(id: string) {
  return id.replace(/\s*\(.*$/, "").trim() || "Controller";
}

const layoutNames: Record<PadLayout, string> = {
  xbox: "Xbox layout",
  playstation: "PlayStation layout",
  nintendo: "Nintendo layout",
  standard: "Standard layout",
};

// Button order follows the standard gamepad map: face buttons, shoulders, middle, sticks, d-pad, home.
const labels: Record<PadLayout, string[]> = {
  xbox: ["A", "B", "X", "Y", "LB", "RB", "LT", "RT", "View", "Menu", "LS", "RS", "Up", "Down", "Left", "Right", "Home", "Share"],
  playstation: ["Cross", "Circle", "Square", "Triangle", "L1", "R1", "L2", "R2", "Create", "Options", "L3", "R3", "Up", "Down", "Left", "Right", "Home", "Touchpad"],
  nintendo: ["B", "A", "Y", "X", "L", "R", "ZL", "ZR", "Minus", "Plus", "LS", "RS", "Up", "Down", "Left", "Right", "Home", "Capture"],
  standard: ["A", "B", "X", "Y", "LB", "RB", "LT", "RT", "Back", "Start", "LS", "RS", "Up", "Down", "Left", "Right", "Home", "Extra"],
};

export const EDGE_SLOTS = 72;

/** Keeps the furthest the stick reached in each direction. Small pushes are ignored. */
export function recordEdge(edge: number[], x: number, y: number) {
  const reach = Math.hypot(x, y);
  if (reach < 0.5) {
    return edge;
  }
  const turn = (Math.atan2(y, x) + 2 * Math.PI) % (2 * Math.PI);
  const slot = Math.floor((turn / (2 * Math.PI)) * EDGE_SLOTS) % EDGE_SLOTS;
  if (reach <= edge[slot]) {
    return edge;
  }
  const next = edge.slice();
  next[slot] = reach;
  return next;
}

/** How far the edge strays from a perfect circle, on average, in percent. */
export function edgeResult(edge: number[]) {
  const reached = edge.filter((reach) => reach > 0);
  const error = reached.length ? (reached.reduce((sum, reach) => sum + Math.abs(reach - 1), 0) / reached.length) * 100 : 0;
  return { covered: reached.length / EDGE_SLOTS, error };
}

const emptyEdge = () => new Array<number>(EDGE_SLOTS).fill(0);

interface PadState {
  index: number;
  id: string;
  standard: boolean;
  buttons: { pressed: boolean; value: number }[];
  axes: number[];
  canVibrate: boolean;
}

interface Rumble {
  playEffect?: (type: string, params: Record<string, number>) => Promise<unknown>;
}

function connected() {
  return Array.from(navigator.getGamepads?.() ?? []).filter((pad): pad is Gamepad => Boolean(pad));
}

function snapshot(pad: Gamepad): PadState {
  const rumble = (pad as Gamepad & { vibrationActuator?: Rumble }).vibrationActuator;
  return {
    index: pad.index,
    id: pad.id,
    standard: pad.mapping === "standard",
    buttons: pad.buttons.map((button) => ({ pressed: button.pressed, value: button.value })),
    axes: [...pad.axes],
    canVibrate: typeof rumble?.playEffect === "function",
  };
}

const cardClass = "rounded-lg border border-border bg-background p-4";
const buttonClass = "min-h-11 cursor-pointer rounded-md border border-border px-4 font-medium disabled:cursor-not-allowed disabled:opacity-50";

export function ControllerTest() {
  const [slots, setSlots] = useState<(string | null)[]>([null, null, null, null]);
  const [chosen, setChosen] = useState<number | null>(null);
  const [pad, setPad] = useState<PadState | null>(null);
  const [seen, setSeen] = useState<Set<number>>(new Set());
  const [edges, setEdges] = useState<[number[], number[]]>([emptyEdge(), emptyEdge()]);
  const chosenRef = useRef<number | null>(null);

  useEffect(() => {
    chosenRef.current = chosen;
  }, [chosen]);

  useEffect(() => {
    let frame = 0;
    const tick = () => {
      const pads = connected();
      setSlots((current) => {
        const next = [0, 1, 2, 3].map((index) => pads.find((item) => item.index === index)?.id ?? null);
        return next.every((id, index) => id === current[index]) ? current : next;
      });

      const wanted = chosenRef.current;
      const live = pads.find((item) => item.index === wanted) ?? pads[0];
      if (!live) {
        setPad(null);
      } else {
        if (live.index !== wanted) {
          setChosen(live.index);
        }
        const state = snapshot(live);
        setPad(state);
        const pressed = state.buttons.flatMap((button, index) => (button.pressed ? [index] : []));
        if (pressed.length > 0) {
          setSeen((current) => (pressed.every((index) => current.has(index)) ? current : new Set([...current, ...pressed])));
        }
        const [lx = 0, ly = 0, rx = 0, ry = 0] = state.axes;
        setEdges((current) => {
          const left = recordEdge(current[0], lx, ly);
          const right = recordEdge(current[1], rx, ry);
          return left === current[0] && right === current[1] ? current : [left, right];
        });
      }
      frame = requestAnimationFrame(tick);
    };
    frame = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(frame);
  }, []);

  function choose(index: number) {
    setChosen(index);
    setSeen(new Set());
    setEdges([emptyEdge(), emptyEdge()]);
  }

  function vibrate(weak: number, strong: number) {
    const live = connected().find((item) => item.index === pad?.index) as (Gamepad & { vibrationActuator?: Rumble }) | undefined;
    void live?.vibrationActuator
      ?.playEffect?.("dual-rumble", { startDelay: 0, duration: 700, weakMagnitude: weak, strongMagnitude: strong })
      .catch(() => undefined);
  }

  const layout = pad ? detectLayout(pad.id) : "standard";
  const names = labels[layout];

  return (
    <section aria-labelledby="controller-title" className="rounded-lg border border-border bg-card p-6">
      <h2 id="controller-title" className="text-lg font-semibold">
        Controller
      </h2>
      <div role="group" aria-label="Controller slots" className="mt-3 flex flex-wrap gap-2">
        {slots.map((id, index) => (
          <button
            key={index}
            type="button"
            disabled={!id}
            aria-pressed={id !== null && pad?.index === index}
            onClick={() => choose(index)}
            className={`min-h-9 max-w-full cursor-pointer truncate rounded-full border px-3 text-sm disabled:cursor-default ${
              id !== null && pad?.index === index ? "border-primary bg-primary text-on-primary" : "border-border text-muted-foreground"
            }`}
          >
            {index + 1} · {id ? padName(id) : "empty"}
          </button>
        ))}
      </div>

      {!pad ? (
        <p className="mt-4 text-muted-foreground">Connect a controller and press any button on it. Windows only shows it to apps after a press.</p>
      ) : (
        <>
          <p className="mt-3 text-sm text-muted-foreground">
            {layoutNames[layout]} detected{pad.standard ? "" : ". This controller doesn't report a standard layout, so buttons may not match the drawing"}.
          </p>
          <div className={`${cardClass} mt-3 flex justify-center`}>
            <PadDrawing layout={layout} pad={pad} />
          </div>

          <div className="mt-3 grid gap-3 lg:grid-cols-[1.3fr_1fr_1fr]">
            <div className={cardClass}>
              <h3 className="font-semibold">Sticks</h3>
              <div className="mt-2 flex flex-wrap justify-around gap-3">
                <StickChart label="Left" x={pad.axes[0] ?? 0} y={pad.axes[1] ?? 0} edge={edges[0]} />
                <StickChart label="Right" x={pad.axes[2] ?? 0} y={pad.axes[3] ?? 0} edge={edges[1]} />
              </div>
            </div>
            <div className={cardClass}>
              <h3 className="font-semibold">Triggers</h3>
              <TriggerBar label={names[6]} value={pad.buttons[6]?.value ?? 0} />
              <TriggerBar label={names[7]} value={pad.buttons[7]?.value ?? 0} />
            </div>
            <div className={cardClass}>
              <h3 className="font-semibold">Drift</h3>
              <Drift label="Left" x={pad.axes[0] ?? 0} y={pad.axes[1] ?? 0} />
              <Drift label="Right" x={pad.axes[2] ?? 0} y={pad.axes[3] ?? 0} />
              <p className="mt-1 text-sm text-muted-foreground">Let go of both sticks. Under 0.05 is fine.</p>
            </div>
            <div className={`${cardClass} lg:col-span-2`}>
              <h3 className="font-semibold">Buttons</h3>
              <p className="text-sm text-muted-foreground">
                {pad.buttons.filter((_, index) => seen.has(index)).length} of {pad.buttons.length} pressed so far.
              </p>
              <ul aria-label="Buttons" className="mt-2 flex flex-wrap gap-1.5">
                {pad.buttons.map((button, index) => (
                  <li
                    key={index}
                    data-testid={`pad-button-${index}`}
                    data-pressed={button.pressed}
                    className={`rounded-md border px-2 py-1 text-sm transition-colors duration-100 ${
                      button.pressed
                        ? "border-primary bg-primary text-on-primary"
                        : seen.has(index)
                          ? "border-border bg-muted font-semibold"
                          : "border-border text-muted-foreground"
                    }`}
                  >
                    {pad.standard ? (names[index] ?? `Button ${index}`) : `Button ${index}`}
                  </li>
                ))}
              </ul>
            </div>
            <div className={cardClass}>
              <h3 className="font-semibold">Vibration</h3>
              {pad.canVibrate ? (
                <div className="mt-2 flex flex-wrap gap-2">
                  <button type="button" onClick={() => vibrate(1, 0)} className={buttonClass}>
                    Weak
                  </button>
                  <button type="button" onClick={() => vibrate(0, 1)} className={buttonClass}>
                    Strong
                  </button>
                </div>
              ) : (
                <p className="mt-1 text-sm text-muted-foreground">This controller doesn't let apps make it vibrate.</p>
              )}
            </div>
            <div className={`${cardClass} lg:col-span-3`}>
              <h3 className="font-semibold">Circularity</h3>
              <div className="mt-1 flex flex-wrap items-center gap-x-6 gap-y-2">
                <Circularity label="Left" edge={edges[0]} />
                <Circularity label="Right" edge={edges[1]} />
                <button type="button" onClick={() => setEdges([emptyEdge(), emptyEdge()])} className={buttonClass}>
                  Start again
                </button>
              </div>
              <p className="mt-2 text-sm text-muted-foreground">
                Spin each stick slowly around its edge a few times. Under 5% is good. A higher number means the stick doesn't reach evenly in every direction.
              </p>
            </div>
          </div>
        </>
      )}
    </section>
  );
}

function StickChart({ label, x, y, edge }: { label: string; x: number; y: number; edge: number[] }) {
  const size = 120;
  const middle = size / 2;
  const radius = middle - 10;
  const outline = edge.some((reach) => reach > 0)
    ? edge
        .map((reach, slot) => {
          const turn = ((slot + 0.5) / EDGE_SLOTS) * 2 * Math.PI;
          const distance = Math.min(reach || 0, 1.45) * radius;
          return `${slot ? "L" : "M"}${(middle + Math.cos(turn) * distance).toFixed(1)} ${(middle + Math.sin(turn) * distance).toFixed(1)}`;
        })
        .join("") + "Z"
    : "";

  return (
    <figure className="text-center">
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} role="img" aria-label={`${label} stick at ${x.toFixed(2)}, ${y.toFixed(2)}`}>
        <circle cx={middle} cy={middle} r={radius} className="fill-card stroke-border" />
        <line x1={middle} y1={10} x2={middle} y2={size - 10} className="stroke-track" />
        <line x1={10} y1={middle} x2={size - 10} y2={middle} className="stroke-track" />
        {outline && <path d={outline} fill="none" strokeWidth={1.5} className="stroke-warning" />}
        <circle cx={middle + x * radius} cy={middle + y * radius} r={6} className="fill-primary" />
      </svg>
      <figcaption className="text-sm text-muted-foreground tabular-nums">
        {label} {x.toFixed(2)}, {y.toFixed(2)}
      </figcaption>
    </figure>
  );
}

function TriggerBar({ label, value }: { label: string; value: number }) {
  const percent = Math.round(value * 100);
  return (
    <div className="mt-2">
      <p className="flex justify-between text-sm">
        <span className="text-muted-foreground">{label}</span>
        <span className="tabular-nums">{percent}%</span>
      </p>
      <div role="meter" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent} className="mt-1 h-2 overflow-hidden rounded-full bg-track">
        <div className="h-full bg-primary" style={{ width: `${percent}%` }} />
      </div>
    </div>
  );
}

function Drift({ label, x, y }: { label: string; x: number; y: number }) {
  const reach = Math.hypot(x, y);
  return (
    <p className="mt-1 flex justify-between">
      <span className="text-muted-foreground">{label}</span>
      <span className={`font-semibold tabular-nums ${reach < 0.05 ? "text-success" : reach < 0.15 ? "text-warning" : "text-muted-foreground"}`}>
        {reach.toFixed(2)}
      </span>
    </p>
  );
}

function Circularity({ label, edge }: { label: string; edge: number[] }) {
  const { covered, error } = edgeResult(edge);
  return (
    <p>
      {label}{" "}
      {covered >= 0.9 ? (
        <b className={error < 5 ? "text-success" : "text-warning"}>{error.toFixed(1)}% average error</b>
      ) : (
        <span className="text-muted-foreground tabular-nums">{Math.round(covered * 100)}% of the edge covered</span>
      )}
    </p>
  );
}

/* ---------- The drawing: TuneForge's own outline, shaped like each family of controller. ---------- */

interface Spot {
  x: number;
  y: number;
}

interface Shape {
  body: string;
  bumpers: [string, string];
  triggers: [number, number];
  leftStick: Spot;
  rightStick: Spot;
  dpad: Spot;
  face: Spot;
  home: Spot;
  back: Spot;
  start: Spot;
  extra: Spot;
  touchpad?: boolean;
}

const offsetShape: Shape = {
  body:
    "M120 58C150 50 250 50 280 58C330 66 360 88 372 138C385 193 392 243 360 258C335 270 312 253 296 228C284 210 270 200 250 200L150 200C130 200 116 210 104 228C88 253 65 270 40 258C8 243 15 193 28 138C40 88 70 66 120 58Z",
  bumpers: ["M56 80Q82 48 146 44", "M344 80Q318 48 254 44"],
  triggers: [84, 260],
  leftStick: { x: 108, y: 112 },
  rightStick: { x: 248, y: 162 },
  dpad: { x: 152, y: 162 },
  face: { x: 292, y: 112 },
  home: { x: 200, y: 80 },
  back: { x: 172, y: 114 },
  start: { x: 228, y: 114 },
  extra: { x: 200, y: 138 },
};

const twinShape: Shape = {
  body:
    "M95 60C130 52 160 58 200 58C240 58 270 52 305 60C345 68 362 98 372 138C385 188 398 240 370 258C345 273 320 256 305 230C292 208 278 196 255 196L145 196C122 196 108 208 95 230C80 256 55 273 30 258C2 240 15 188 28 138C38 98 55 68 95 60Z",
  bumpers: ["M36 82Q54 50 114 46", "M364 82Q346 50 286 46"],
  triggers: [62, 282],
  leftStick: { x: 150, y: 164 },
  rightStick: { x: 250, y: 164 },
  dpad: { x: 92, y: 116 },
  face: { x: 308, y: 116 },
  home: { x: 200, y: 160 },
  back: { x: 132, y: 72 },
  start: { x: 268, y: 72 },
  extra: { x: 200, y: 92 },
  touchpad: true,
};

const TRIGGER_TOP = 10;
const idle = "fill-card stroke-border";
const lit = "fill-primary stroke-primary";
const ink = (on: boolean) => (on ? "fill-on-primary" : "fill-muted-foreground");

function PadDrawing({ layout, pad }: { layout: PadLayout; pad: PadState }) {
  const shape = layout === "playstation" ? twinShape : offsetShape;
  const names = labels[layout];
  const on = (index: number) => pad.buttons[index]?.pressed ?? false;
  const value = (index: number) => pad.buttons[index]?.value ?? 0;
  const axes = pad.axes;

  return (
    <svg viewBox="0 0 400 280" className="h-auto w-full max-w-[34rem]" aria-hidden="true">
      <defs>
        {[0, 1].map((side) => (
          <clipPath key={side} id={`trigger-${side}`}>
            <rect x={shape.triggers[side]} y={TRIGGER_TOP} width={56} height={30} rx={10} />
          </clipPath>
        ))}
      </defs>

      {[6, 7].map((index, side) => (
        <g key={index}>
          <rect x={shape.triggers[side]} y={TRIGGER_TOP} width={56} height={30} rx={10} strokeWidth={2} className="fill-track stroke-border" />
          <rect
            x={shape.triggers[side]}
            y={TRIGGER_TOP + 30 * (1 - value(index))}
            width={56}
            height={30 * value(index)}
            clipPath={`url(#trigger-${side})`}
            className="fill-primary"
          />
          <text x={shape.triggers[side] + 28} y={TRIGGER_TOP + 17} textAnchor="middle" fontSize={11} fontWeight={600} className={ink(value(index) > 0.6)}>
            {names[index]}
          </text>
        </g>
      ))}

      {[4, 5].map((index, side) => (
        <g key={index}>
          <path d={shape.bumpers[side]} fill="none" strokeWidth={14} strokeLinecap="round" className={on(index) ? "stroke-primary" : "stroke-border"} />
          <path d={shape.bumpers[side]} fill="none" strokeWidth={10} strokeLinecap="round" className={on(index) ? "stroke-primary" : "stroke-track"} />
        </g>
      ))}

      <path d={shape.body} strokeWidth={2} className="fill-muted stroke-border" />

      {shape.touchpad && <rect x={148} y={62} width={104} height={56} rx={10} strokeWidth={2} className={on(17) ? lit : idle} />}

      <Stick at={shape.leftStick} x={axes[0] ?? 0} y={axes[1] ?? 0} pressed={on(10)} />
      <Stick at={shape.rightStick} x={axes[2] ?? 0} y={axes[3] ?? 0} pressed={on(11)} />

      <DPad at={shape.dpad} separate={layout === "playstation"} pressed={[on(12), on(15), on(13), on(14)]} />
      <FaceButtons at={shape.face} layout={layout} pressed={[on(3), on(1), on(0), on(2)]} names={[names[3], names[1], names[0], names[2]]} />

      <circle cx={shape.home.x} cy={shape.home.y} r={layout === "playstation" ? 8 : 12} strokeWidth={2} className={on(16) ? lit : idle} />
      {layout === "playstation" ? (
        <>
          <rect x={shape.back.x - 4} y={shape.back.y - 8} width={8} height={16} rx={4} strokeWidth={1.5} className={on(8) ? lit : idle} />
          <rect x={shape.start.x - 4} y={shape.start.y - 8} width={8} height={16} rx={4} strokeWidth={1.5} className={on(9) ? lit : idle} />
        </>
      ) : (
        <>
          <circle cx={shape.back.x} cy={shape.back.y} r={7} strokeWidth={1.5} className={on(8) ? lit : idle} />
          <circle cx={shape.start.x} cy={shape.start.y} r={7} strokeWidth={1.5} className={on(9) ? lit : idle} />
          <rect x={shape.extra.x - 7} y={shape.extra.y - 4} width={14} height={8} rx={4} strokeWidth={1.5} className={on(17) ? lit : idle} />
        </>
      )}
    </svg>
  );
}

function Stick({ at, x, y, pressed }: { at: Spot; x: number; y: number; pressed: boolean }) {
  return (
    <g>
      <circle cx={at.x} cy={at.y} r={25} strokeWidth={2} className="fill-background stroke-border" />
      <circle cx={at.x + x * 9} cy={at.y + y * 9} r={16} strokeWidth={2} className={pressed ? lit : "fill-track stroke-border"} />
      <circle cx={at.x + x * 9} cy={at.y + y * 9} r={11} fill="none" strokeWidth={1} className={pressed ? "stroke-on-primary" : "stroke-border"} />
    </g>
  );
}

/** Up, right, down, left. */
function DPad({ at, separate, pressed }: { at: Spot; separate: boolean; pressed: boolean[] }) {
  const arm = separate
    ? `M${at.x - 8} ${at.y - 28}H${at.x + 8}V${at.y - 13}L${at.x} ${at.y - 6}L${at.x - 8} ${at.y - 13}Z`
    : `M${at.x - 8} ${at.y - 25}H${at.x + 8}V${at.y - 8}H${at.x - 8}Z`;
  return (
    <g>
      {!separate && <rect x={at.x - 8} y={at.y - 8} width={16} height={16} className="fill-card" />}
      {pressed.map((down, side) => (
        <path key={side} d={arm} strokeWidth={1.5} strokeLinejoin="round" transform={`rotate(${side * 90} ${at.x} ${at.y})`} className={down ? lit : idle} />
      ))}
    </g>
  );
}

/** Top, right, bottom, left. PlayStation-style buttons get shapes instead of letters. */
function FaceButtons({ at, layout, pressed, names }: { at: Spot; layout: PadLayout; pressed: boolean[]; names: string[] }) {
  const spots = [
    { x: at.x, y: at.y - 23 },
    { x: at.x + 23, y: at.y },
    { x: at.x, y: at.y + 23 },
    { x: at.x - 23, y: at.y },
  ];
  return (
    <g>
      {spots.map((spot, side) => (
        <g key={side}>
          <circle cx={spot.x} cy={spot.y} r={11} strokeWidth={1.5} className={pressed[side] ? lit : idle} />
          {layout === "playstation" ? (
            <ShapeMark at={spot} side={side} on={pressed[side]} />
          ) : (
            <text x={spot.x} y={spot.y + 4} textAnchor="middle" fontSize={11} fontWeight={600} className={ink(pressed[side])}>
              {names[side]}
            </text>
          )}
        </g>
      ))}
    </g>
  );
}

function ShapeMark({ at, side, on }: { at: Spot; side: number; on: boolean }) {
  const stroke = on ? "stroke-on-primary" : "stroke-muted-foreground";
  const common = { fill: "none", strokeWidth: 1.6, className: stroke };
  if (side === 0) {
    return <path d={`M${at.x} ${at.y - 5}L${at.x + 5} ${at.y + 4}H${at.x - 5}Z`} strokeLinejoin="round" {...common} />;
  }
  if (side === 1) {
    return <circle cx={at.x} cy={at.y} r={5} {...common} />;
  }
  if (side === 2) {
    return <path d={`M${at.x - 4.5} ${at.y - 4.5}L${at.x + 4.5} ${at.y + 4.5}M${at.x + 4.5} ${at.y - 4.5}L${at.x - 4.5} ${at.y + 4.5}`} strokeLinecap="round" {...common} />;
  }
  return <rect x={at.x - 4.5} y={at.y - 4.5} width={9} height={9} {...common} />;
}
