import { useCallback, useEffect, useState } from "react";
import { bridge, type DisplayMode, type GameBoosts, type GameEntry, type GamingOverview, type OperationResult } from "./bridge";
import { BiosCard, TurboCard } from "./ExtraPanels";
import { TweaksPage } from "./TweaksPage";

type Tab = "games" | "system" | "tweaks" | "anti-cheat";

const tabs: { id: Tab; label: string }[] = [
  { id: "games", label: "Games" },
  { id: "system", label: "Power and focus" },
  { id: "tweaks", label: "Tweaks" },
  { id: "anti-cheat", label: "Anti-cheat" },
];

const boostOptions: { key: keyof GameBoosts; label: string; note: string }[] = [
  { key: "highPerformanceGpu", label: "Use the fast graphics card", note: "Tells Windows to run this game on your best GPU." },
  { key: "disableFullscreenOptimizations", label: "Turn off fullscreen optimizations", note: "Can help older games. Some newer games run worse." },
  { key: "powerBoost", label: "High performance power while playing", note: "Goes back to your plan when the game closes." },
  { key: "focus", label: "Focus mode while playing", note: "Silences notifications and closes the apps you picked." },
  { key: "highPriority", label: "High priority", note: "Gives the game more CPU time. Skipped for games with anti-cheat." },
];

const buttonClass =
  "min-h-11 shrink-0 cursor-pointer rounded-md border border-border px-4 font-medium disabled:cursor-not-allowed disabled:opacity-60";

export function GamingPage({ initialTab, initialQuery }: { initialTab?: string; initialQuery?: string }) {
  const [tab, setTab] = useState<Tab>((initialTab as Tab) ?? "games");

  return (
    <div>
      <div role="tablist" aria-label="Gaming sections" className="flex flex-wrap gap-2">
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
        {tab === "games" && <GamesTab />}
        {tab === "system" && <SystemTab />}
        {tab === "tweaks" && <TweaksPage categories={["gaming"]} initialQuery={initialQuery} />}
        {tab === "anti-cheat" && <AntiCheatTab />}
      </div>
    </div>
  );
}

function GamesTab() {
  const [games, setGames] = useState<GameEntry[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState<string | null>(null);

  useEffect(() => {
    bridge.gamesList().then(setGames, (reason: Error) => setError(reason.message));
  }, []);

  async function change(game: GameEntry, key: keyof GameBoosts, value: boolean) {
    const boosts = { ...game.boosts, [key]: value };
    setError(null);
    try {
      await bridge.gamesSave(game.id, boosts);
      setGames((current) => current?.map((item) => (item.id === game.id ? { ...item, boosts } : item)) ?? null);
    } catch (reason) {
      setError((reason as Error).message);
    }
  }

  return (
    <div>
      <TurboCard />
      <p className="mb-3 text-muted-foreground">
        Boosts that apply while playing need TuneForge open or in the tray, and everything goes back when the game closes.
      </p>
      <div role="status">{error && <p className="mb-3 font-medium text-destructive">{error}</p>}</div>
      {!games && !error && <p className="text-muted-foreground">Looking for games.</p>}
      {games?.length === 0 && <p className="text-muted-foreground">No Steam, Epic or GOG games were found.</p>}
      <div className="flex flex-col gap-2">
        {games?.map((game) => {
          const on = Object.values(game.boosts).filter(Boolean).length;
          return (
            <div key={game.id} className="rounded-md border border-border bg-card p-3">
              <div className="flex items-center justify-between gap-3">
                <div className="min-w-0">
                  <p className="font-medium break-words">{game.title}</p>
                  <p className="text-sm text-muted-foreground">
                    {game.launcher}
                    {game.antiCheat ? `, ${game.antiCheat}` : ""}
                    {on > 0 ? `, ${on} on` : ""}
                  </p>
                </div>
                <button
                  type="button"
                  aria-expanded={open === game.id}
                  aria-label={`Boosts for ${game.title}`}
                  onClick={() => setOpen(open === game.id ? null : game.id)}
                  className={buttonClass}
                >
                  Boosts
                </button>
              </div>
              {open === game.id && (
                <div className="mt-3 flex flex-col gap-3">
                  <p className="text-sm break-all text-muted-foreground">{game.executable}</p>
                  {game.launcher === "Steam" && (
                    <div className="flex items-center justify-between gap-3">
                      <span className="min-w-0">
                        <span className="block font-medium">Repair this game</span>
                        <span className="block text-sm text-muted-foreground">Asks Steam to check its files and replace damaged ones.</span>
                      </span>
                      <button type="button" aria-label={`Repair ${game.title}`} onClick={() => bridge.gamesVerify(game.id).catch((reason: Error) => setError(reason.message))} className={buttonClass}>
                        Repair
                      </button>
                    </div>
                  )}
                  {boostOptions.map((option) => {
                    const blocked = option.key === "highPriority" && Boolean(game.antiCheat);
                    return (
                      <label key={option.key} className={`flex min-h-11 items-start gap-3 ${blocked ? "opacity-60" : "cursor-pointer"}`}>
                        <input
                          type="checkbox"
                          checked={game.boosts[option.key] && !blocked}
                          disabled={blocked}
                          onChange={(event) => change(game, option.key, event.target.checked)}
                          className="mt-1 size-5 shrink-0"
                        />
                        <span>
                          <span className="block font-medium">{option.label}</span>
                          <span className="block text-sm text-muted-foreground">{option.note}</span>
                        </span>
                      </label>
                    );
                  })}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

const resizeBarText = {
  on: "Resize BAR is on.",
  off: "Resize BAR is off. It's turned on in your BIOS, and it can help in newer games.",
  unknown: "TuneForge can only check Resize BAR on NVIDIA graphics cards.",
};

function SystemTab() {
  const [overview, setOverview] = useState<GamingOverview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<OperationResult | string | null>(null);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [planName, setPlanName] = useState("");
  const [display, setDisplay] = useState<DisplayMode | null>(null);
  const [parking, setParking] = useState<number | null>(null);

  const readHardware = useCallback(() => {
    bridge.displayRead().then(setDisplay, () => setDisplay(null));
    bridge.powerParking().then((status) => setParking(status.minimumCoresPercent ?? null), () => setParking(null));
  }, []);
  useEffect(readHardware, [readHardware]);

  const reload = useCallback(() => {
    bridge.gamingOverview().then(
      (next) => {
        setOverview(next);
        setPicked(new Set(next.focusApps));
      },
      (reason: Error) => setError(reason.message),
    );
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

  async function toggleApp(name: string) {
    const next = new Set(picked);
    if (!next.delete(name)) {
      next.add(name);
    }
    setPicked(next);
    await run(() => bridge.focusSave([...next]));
  }

  if (error) {
    return <p role="alert" className="font-medium text-destructive">{error}</p>;
  }
  if (!overview) {
    return <p className="text-muted-foreground">Reading power plans.</p>;
  }

  const text = typeof result === "string" ? result : result?.message;
  const failed = typeof result === "string" || result?.ok === false;
  const appNames = [...new Set([...overview.focusApps, ...overview.runningApps])].sort((a, b) => a.localeCompare(b));

  return (
    <div className="flex flex-col gap-6">
      <div role="status">{text && <p className={`font-medium ${failed ? "text-destructive" : "text-success"}`}>{text}</p>}</div>

      <section aria-labelledby="power-title" className="rounded-lg border border-border bg-card p-6">
        <h2 id="power-title" className="text-lg font-semibold">
          Power plan
        </h2>
        <div className="mt-3 flex flex-col gap-2">
          {overview.plans.map((plan) => (
            <div key={plan.id} className="flex min-h-11 items-center justify-between gap-3">
              <p className="min-w-0 font-medium break-words">{plan.name}</p>
              {plan.active ? (
                <span className="shrink-0 text-sm font-medium text-success">Active</span>
              ) : (
                <button
                  type="button"
                  disabled={busy}
                  aria-label={`Use ${plan.name}`}
                  onClick={() => run(() => bridge.powerSet(plan.id))}
                  className={buttonClass}
                >
                  Use
                </button>
              )}
            </div>
          ))}
        </div>
        <div className="mt-3 flex flex-wrap gap-2">
          <button type="button" disabled={busy} onClick={() => run(bridge.powerUltimate)} className={buttonClass}>
            Add Ultimate Performance
          </button>
          <input
            aria-label="New plan name"
            value={planName}
            onChange={(event) => setPlanName(event.target.value)}
            placeholder="New plan name"
            className="min-h-11 w-44 rounded-md border border-border bg-background px-3"
          />
          <button
            type="button"
            disabled={busy || planName.trim() === ""}
            onClick={() => run(() => bridge.powerCreate(planName)).then(() => setPlanName(""))}
            className={buttonClass}
          >
            Create from current plan
          </button>
        </div>
        <p className="mt-2 text-sm text-muted-foreground">A new plan starts as a copy of your active plan.</p>
      </section>

      <section aria-labelledby="cpu-title" className="rounded-lg border border-border bg-card p-6">
        <h2 id="cpu-title" className="text-lg font-semibold">
          Display and CPU
        </h2>
        <div className="mt-3 flex items-center justify-between gap-3">
          <div className="min-w-0">
            <p className="font-medium">
              {display ? `Refresh rate: ${display.currentHz} Hz of ${display.bestHz} Hz` : "Refresh rate not reported"}
            </p>
            <p className="text-sm text-muted-foreground">
              {display?.name}. Windows doesn't always pick your monitor's top speed.
            </p>
          </div>
          {display && display.currentHz < display.bestHz && (
            <button type="button" disabled={busy} onClick={() => run(bridge.displayBest).then(readHardware)} className={buttonClass}>
              Use {display.bestHz} Hz
            </button>
          )}
        </div>
        <div className="mt-3 flex items-center justify-between gap-3">
          <div className="min-w-0">
            <p className="font-medium">
              {parking == null ? "Core parking not reported" : parking < 100 ? `Core parking is on (${parking}% of cores stay awake)` : "Core parking is off"}
            </p>
            <p className="text-sm text-muted-foreground">Parked CPU cores save power, but can cause brief stutters while waking.</p>
          </div>
          {parking != null && parking < 100 && (
            <button type="button" disabled={busy} onClick={() => run(bridge.powerKeepCores).then(readHardware)} className={buttonClass}>
              Keep cores awake
            </button>
          )}
        </div>
      </section>

      <section aria-labelledby="focus-title" className="rounded-lg border border-border bg-card p-6">
        <h2 id="focus-title" className="text-lg font-semibold">
          Focus mode apps
        </h2>
        <p className="mt-1 text-muted-foreground">
          These close when a game with focus mode starts. They're asked to close normally, so they can save your work, and they don't reopen.
        </p>
        {appNames.length === 0 && <p className="mt-2 text-muted-foreground">No apps are open right now.</p>}
        <div className="mt-3 flex flex-col gap-1">
          {appNames.map((name) => (
            <label key={name} className="flex min-h-11 cursor-pointer items-center gap-3">
              <input
                type="checkbox"
                checked={picked.has(name)}
                disabled={busy}
                onChange={() => toggleApp(name)}
                className="size-5 shrink-0"
              />
              <span className="font-medium break-words">{name}</span>
            </label>
          ))}
        </div>
      </section>

      <section aria-labelledby="bar-title" className="rounded-lg border border-border bg-card p-6">
        <h2 id="bar-title" className="text-lg font-semibold">
          Resize BAR
        </h2>
        <p className="mt-1 text-muted-foreground">{resizeBarText[overview.resizeBar]}</p>
      </section>

      <BiosCard />
    </div>
  );
}

const antiCheatRows: [string, string, string][] = [
  ["Fast graphics card", "Allowed", "A Windows setting. The game isn't touched."],
  ["Fullscreen optimizations", "Allowed", "A Windows compatibility setting. The game isn't touched."],
  ["Power plan and notifications", "Allowed", "Changes Windows, not the game."],
  ["Closing other apps", "Allowed", "Only affects the apps you picked."],
  ["High priority", "Skipped", "It targets the game's own process, so TuneForge leaves protected games alone, and skips it while any anti-cheat is running."],
  ["Free memory", "Skipped", "It touches other programs, so it won't run at all while a game or anti-cheat is open."],
  ["Anti-cheat services and programs", "Locked", "TuneForge won't end, disable or close them."],
  ["Boot, timer and firmware switches", "Off, with a warning", "Platform tick, dynamic tick, legacy boot menu, WPBT and global timer resolution work where Vanguard runs. They stay off unless you confirm in a warning window."],
];

function AntiCheatTab() {
  return (
    <div>
      <p className="mb-3 text-muted-foreground">
        TuneForge never reads or changes a game's memory or files, and has no overlay. These rules apply to any game where Easy Anti-Cheat, BattlEye, Vanguard or similar is found. They come from how each boost works, and haven't been tested against each anti-cheat.
      </p>
      <div className="overflow-x-auto rounded-md border border-border bg-card p-4">
        <table className="w-full text-left">
          <thead className="text-sm text-muted-foreground">
            <tr>
              <th scope="col" className="pr-4 font-medium">Boost</th>
              <th scope="col" className="pr-4 font-medium">With anti-cheat</th>
              <th scope="col" className="font-medium">Why</th>
            </tr>
          </thead>
          <tbody>
            {antiCheatRows.map(([boost, state, why]) => (
              <tr key={boost}>
                <td className="py-1 pr-4 font-medium">{boost}</td>
                <td className="py-1 pr-4">{state}</td>
                <td className="py-1 text-muted-foreground">{why}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
