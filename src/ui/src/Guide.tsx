import { ChevronDown, CircleHelp, Search } from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";
import { bridge, type ImportResult, type Tweak } from "./bridge";
import { pageHelp } from "./help";
import { type ProfileName, profileIds } from "./profiles";
import { ReviewDialog } from "./ReviewDialog";
import { matchesText, presetSelection } from "./selection";

const buttonClass = "min-h-11 cursor-pointer rounded-md border border-border px-4 font-medium disabled:cursor-not-allowed disabled:opacity-60";
const primaryClass = "min-h-11 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary disabled:cursor-not-allowed disabled:opacity-60";
const guideKey = "tuneforge.guide-done";

export function PageHelp({ page }: { page: string }) {
  const [open, setOpen] = useState(false);
  const steps = pageHelp[page];
  if (!steps) {
    return null;
  }

  return (
    <div className="mb-5">
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen(!open)}
        className="flex cursor-pointer items-center gap-1.5 text-muted-foreground hover:text-foreground"
      >
        <CircleHelp aria-hidden="true" className="size-4" />
        How this works
        <ChevronDown aria-hidden="true" className={`size-4 transition-transform duration-150 ${open ? "rotate-180" : ""}`} />
      </button>
      {open && (
        <ol className="mt-2 ml-5 flex list-decimal flex-col gap-1 text-muted-foreground">
          {steps.map((step) => (
            <li key={step}>{step}</li>
          ))}
        </ol>
      )}
    </div>
  );
}

const pageIndex: { page: string; tab?: string; title: string; note: string }[] = [
  { page: "dashboard", title: "Dashboard", note: "health score report live usage" },
  { page: "optimize", title: "Optimize", note: "tweaks presets profiles system switches" },
  { page: "gaming", title: "Games and boosts", note: "game boost priority gpu" },
  { page: "gaming", tab: "system", title: "Power plans and focus mode", note: "ultimate performance core parking refresh rate resize bar" },
  { page: "network", title: "Ping, TCP ping and route test", note: "latency jitter packet loss mtr traceroute" },
  { page: "network", tab: "dns", title: "DNS", note: "benchmark speed cloudflare google flush" },
  { page: "network", tab: "connections", title: "Connections", note: "scanner netstat open ports" },
  { page: "cleanup", title: "Cleanup", note: "junk temp files recycle bin memory ram" },
  { page: "apps", title: "Startup programs", note: "autostart" },
  { page: "apps", tab: "services", title: "Services", note: "windows services" },
  { page: "apps", tab: "features", title: "Windows features", note: "sandbox wsl hyper-v .net" },
  { page: "apps", tab: "built-in", title: "Built-in apps", note: "debloat remove bloatware xbox" },
  { page: "apps", tab: "install", title: "Install apps", note: "winget software" },
  { page: "apps", tab: "installed", title: "Installed apps", note: "update uninstall" },
  { page: "apps", tab: "processes", title: "Processes", note: "task manager end task kill" },
  { page: "privacy", title: "Privacy and security", note: "defender firewall bitlocker telemetry" },
  { page: "repair", title: "Repair", note: "sfc dism chkdsk network reset windows update memory test control panel" },
  { page: "monitor", title: "Disks", note: "health smart storage" },
  { page: "monitor", tab: "errors", title: "Windows errors", note: "crash event" },
  { page: "monitor", tab: "logs", title: "Logs", note: "event viewer" },
  { page: "monitor", tab: "tests", title: "Hardware tests", note: "keyboard mouse monitor dead pixels" },
  { page: "history", title: "History", note: "undo" },
  { page: "settings", title: "Settings", note: "theme tray automations export import updates" },
];

export interface SearchTarget {
  page: string;
  tab?: string;
  query?: string;
}

const pagesForTweak = (tweak: Tweak) => (tweak.category === "gaming" ? "gaming" : "optimize");

export function GlobalSearch({ onOpen }: { onOpen: (target: SearchTarget) => void }) {
  const [query, setQuery] = useState("");
  const [tweaks, setTweaks] = useState<Tweak[]>([]);
  const requested = useRef(false);

  function load() {
    if (!requested.current) {
      requested.current = true;
      bridge.scan().then(setTweaks, () => setTweaks([]));
    }
  }

  const results = useMemo(() => {
    if (query.trim().length < 2) {
      return [];
    }

    const pages = pageIndex
      .filter((item) => matchesText(`${item.title} ${item.note}`, query))
      .map((item) => ({ key: `${item.page}${item.tab}${item.title}`, title: item.title, note: "Page", target: { page: item.page, tab: item.tab } as SearchTarget }));
    const found = tweaks
      .filter((tweak) => matchesText(`${tweak.title} ${tweak.description}`, query))
      .map((tweak) => ({
        key: tweak.id,
        title: tweak.title,
        note: "Tweak",
        target: { page: pagesForTweak(tweak), tab: tweak.category === "gaming" ? "tweaks" : undefined, query: tweak.title } as SearchTarget,
      }));
    return [...pages, ...found].slice(0, 12);
  }, [query, tweaks]);

  return (
    <div className="relative mb-2 px-1">
      <label className="relative block">
        <span className="sr-only">Search everything</span>
        <Search aria-hidden="true" className="absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" />
        <input
          type="search"
          value={query}
          onFocus={load}
          onChange={(event) => setQuery(event.target.value)}
          placeholder="Search everything"
          className="min-h-11 w-full rounded-md border border-border bg-card pr-2 pl-8"
        />
      </label>
      {results.length > 0 && (
        <ul className="absolute top-full right-1 left-1 z-20 mt-1 w-80 rounded-md border border-track bg-card p-1 shadow-2xl shadow-black/60">
          {results.map((result) => (
            <li key={result.key}>
              <button
                type="button"
                onClick={() => {
                  setQuery("");
                  onOpen(result.target);
                }}
                className="flex w-full cursor-pointer items-center justify-between gap-3 rounded-md px-2 py-1.5 text-left hover:bg-muted"
              >
                <span className="min-w-0 truncate">{result.title}</span>
                <span className="shrink-0 text-sm text-muted-foreground">{result.note}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

const questions: { id: string; text: string; note: string; yes: ProfileName[]; no?: ProfileName[] }[] = [
  { id: "games", text: "Do you play games on this PC?", note: "Adds lower-latency settings.", yes: ["gaming"] },
  { id: "laptop", text: "Is this a laptop?", note: "Saves battery on laptops, and speeds up desktops.", yes: ["laptop"], no: ["desktop"] },
  { id: "printer", text: "Do you use a printer?", note: "If not, printing services can be turned off.", yes: [], no: ["noPrinter"] },
  { id: "ai", text: "Do you use Copilot or other Windows AI features?", note: "If not, they can be turned off.", yes: [], no: ["ai"] },
  { id: "privacy", text: "Do you want Windows to collect less about you?", note: "Limits telemetry and personalization.", yes: ["privacy"] },
  { id: "linux", text: "Do you dual-boot with Linux?", note: "Fixes the clock and disk access between systems.", yes: ["dualBoot"] },
];

const tour: [string, string][] = [
  ["Dashboard", "Your PC's health, live usage and quick actions."],
  ["Optimize and Privacy", "Windows settings, with presets and a review before anything changes."],
  ["Gaming and Network", "Per-game boosts, power plans, DNS and connection tests."],
  ["Cleanup, Apps and Repair", "Clear junk, control what's installed, and fix Windows."],
  ["History", "Undo tweaks you've applied."],
];

/** Shown once. Nothing is changed until the person reviews and applies it. */
export function SetupGuide({ onDone }: { onDone: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const [step, setStep] = useState(0);
  const [answers, setAnswers] = useState<Record<string, boolean>>({});
  const [tweaks, setTweaks] = useState<Tweak[] | null>(null);
  const [reviewing, setReviewing] = useState<string[] | null>(null);

  useEffect(() => {
    dialog.current?.showModal();
    bridge.scan().then(setTweaks, () => setTweaks([]));
  }, []);

  function finish() {
    try {
      localStorage.setItem(guideKey, "1");
    } catch {
      // The guide may show again next time.
    }
    onDone();
  }

  const picks = useMemo(() => {
    const names = questions.flatMap((question) => (answers[question.id] === undefined ? [] : answers[question.id] ? question.yes : question.no ?? []));
    const base = tweaks ? presetSelection("minimal", tweaks.filter((tweak) => tweak.category !== "gaming" && tweak.state === "not-applied")) : new Set<string>();
    return [...new Set([...base, ...profileIds(names, tweaks ?? [])])];
  }, [answers, tweaks]);

  if (reviewing) {
    return <ReviewDialog ids={reviewing} onClose={finish} />;
  }

  return (
    <dialog
      ref={dialog}
      aria-labelledby="guide-title"
      onCancel={(event) => {
        event.preventDefault();
        finish();
      }}
      className="m-auto max-h-[85vh] w-[36rem] max-w-[90vw] rounded-lg border border-border bg-card p-6 text-foreground backdrop:bg-black/70"
    >
      {step === 0 && (
        <>
          <h2 id="guide-title" className="text-xl font-semibold">
            Welcome to TuneForge
          </h2>
          <p className="mt-1 text-muted-foreground">Here's where everything is. Every page also has a "How this works" link.</p>
          <dl className="mt-4 flex flex-col gap-2">
            {tour.map(([title, note]) => (
              <div key={title}>
                <dt className="font-medium">{title}</dt>
                <dd className="text-muted-foreground">{note}</dd>
              </div>
            ))}
          </dl>
        </>
      )}
      {step === 1 && (
        <>
          <h2 id="guide-title" className="text-xl font-semibold">
            A few questions
          </h2>
          <p className="mt-1 text-muted-foreground">Your answers pick suggestions. Nothing changes until you review them.</p>
          <div className="mt-4 flex flex-col gap-3">
            {questions.map((question) => (
              <fieldset key={question.id} className="flex items-center justify-between gap-3">
                <legend className="sr-only">{question.text}</legend>
                <div className="min-w-0">
                  <p className="font-medium">{question.text}</p>
                  <p className="text-sm text-muted-foreground">{question.note}</p>
                </div>
                <div className="flex shrink-0 gap-1">
                  {[true, false].map((value) => (
                    <button
                      key={String(value)}
                      type="button"
                      aria-pressed={answers[question.id] === value}
                      aria-label={`${question.text} ${value ? "Yes" : "No"}`}
                      onClick={() => setAnswers({ ...answers, [question.id]: value })}
                      className={`min-h-9 w-12 cursor-pointer rounded-md border font-medium ${
                        answers[question.id] === value ? "border-primary bg-primary text-on-primary" : "border-border"
                      }`}
                    >
                      {value ? "Yes" : "No"}
                    </button>
                  ))}
                </div>
              </fieldset>
            ))}
          </div>
        </>
      )}
      <div className="mt-6 flex items-center justify-between gap-3">
        <button type="button" onClick={finish} className="cursor-pointer text-muted-foreground hover:text-foreground">
          Skip for now
        </button>
        {step === 0 ? (
          <button type="button" onClick={() => setStep(1)} className={primaryClass}>
            Next
          </button>
        ) : (
          <div className="flex gap-2">
            <button type="button" onClick={() => setStep(0)} className={buttonClass}>
              Back
            </button>
            <button type="button" disabled={picks.length === 0} onClick={() => setReviewing(picks)} className={primaryClass}>
              Review {picks.length} suggestions
            </button>
          </div>
        )}
      </div>
    </dialog>
  );
}

export function guideShouldShow() {
  try {
    return localStorage.getItem(guideKey) === null;
  } catch {
    return false;
  }
}

export function BackupCard({ onGuide }: { onGuide: () => void }) {
  const [message, setMessage] = useState<string | null>(null);
  const [reviewing, setReviewing] = useState<string[] | null>(null);

  async function exportFile() {
    try {
      const result = await bridge.settingsExport();
      setMessage(result.saved ? `Saved to ${result.path}` : null);
    } catch (reason) {
      setMessage((reason as Error).message);
    }
  }

  async function importFile() {
    try {
      const result: ImportResult | null = await bridge.settingsImport();
      if (!result) {
        return;
      }

      setMessage(`Loaded ${result.tweaks.length} tweaks and ${result.apps.length} apps.${result.ignored > 0 ? ` ${result.ignored} unknown items were skipped.` : ""}`);
      if (result.tweaks.length > 0) {
        setReviewing(result.tweaks);
      }
    } catch (reason) {
      setMessage((reason as Error).message);
    }
  }

  return (
    <section aria-labelledby="backup-title" className="rounded-lg border border-border bg-card p-6">
      <h2 id="backup-title" className="text-lg font-semibold">
        Guide and backup
      </h2>
      <p className="mt-1 text-muted-foreground">
        Export saves your applied tweaks, app choices and these settings to a file. Importing it on another PC shows the tweaks for review first.
      </p>
      <div className="mt-3 flex flex-wrap gap-2">
        <button type="button" onClick={onGuide} className={buttonClass}>
          Run the setup guide
        </button>
        <button type="button" onClick={exportFile} className={buttonClass}>
          Export settings
        </button>
        <button type="button" onClick={importFile} className={buttonClass}>
          Import settings
        </button>
      </div>
      <div role="status">{message && <p className="mt-2 font-medium">{message}</p>}</div>
      {reviewing && <ReviewDialog ids={reviewing} onClose={() => setReviewing(null)} />}
    </section>
  );
}
