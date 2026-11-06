import {
  AppWindow,
  Bot,
  EyeOff,
  FolderOpen,
  Gamepad2,
  Gauge,
  Globe,
  Lock,
  type LucideIcon,
  Palette,
  PanelBottom,
  RefreshCw,
  Search,
  SlidersHorizontal,
  SquareMenu,
  TriangleAlert,
  Wifi,
} from "lucide-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { bridge, type OperationResult, type Tweak } from "./bridge";
import { HazardDialog, hazardLabel } from "./Hazard";
import { profileChoices, profileIds } from "./profiles";
import { ReviewDialog } from "./ReviewDialog";
import { matchesSearch, type Preset, presetOrder, presetSelection, toggle } from "./selection";

export const categoryLabels: Record<string, string> = {
  privacy: "Privacy",
  ai: "AI features",
  windows: "Windows",
  taskbar: "Taskbar",
  "start-search": "Start and search",
  explorer: "File Explorer",
  appearance: "Appearance",
  update: "Windows Update",
  browser: "Browsers",
  gaming: "Gaming",
  performance: "Performance",
  network: "Network",
  advanced: "Advanced",
};

const categoryNotes: Record<string, string> = {
  advanced: "These change how Windows works at a low level. Only use them if you know why you want them. Read each warning, and keep a restore point turned on.",
};

const categoryIcons: Record<string, LucideIcon> = {
  performance: Gauge,
  network: Wifi,
  advanced: TriangleAlert,
  privacy: EyeOff,
  ai: Bot,
  windows: AppWindow,
  taskbar: PanelBottom,
  "start-search": SquareMenu,
  explorer: FolderOpen,
  appearance: Palette,
  update: RefreshCw,
  browser: Globe,
  gaming: Gamepad2,
};

const groupLabels: Record<string, string> = {
  "taskbar-combine": "Combine taskbar buttons",
  "taskbar-combine-other": "Combine buttons on other displays",
  "taskbar-other-displays": "Taskbar on other displays",
  "explorer-opens-to": "File Explorer opens to",
  "alt-tab": "Tabs in Alt+Tab",
  "taskbar-search": "Taskbar search",
  "drive-letters": "Drive letters",
  "start-all-apps": "Start menu All Apps view",
  spotlight: "Windows Spotlight desktop",
};

const presetLabels: Record<Preset, string> = { minimal: "Minimal", standard: "Standard", advanced: "Advanced" };

export function TweaksPage({
  categories,
  showPresets = false,
  initialQuery = "",
}: {
  categories: string[];
  showPresets?: boolean;
  initialQuery?: string;
}) {
  const [tweaks, setTweaks] = useState<Tweak[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [query, setQuery] = useState(initialQuery);
  const [reviewing, setReviewing] = useState<string[] | null>(null);
  const [confirming, setConfirming] = useState<Tweak | null>(null);

  /** Turning a hazardous tweak on goes through a confirmation window first. Turning it off never does. */
  function change(tweak: Tweak) {
    if (tweak.hazard && !selected.has(tweak.id)) {
      setConfirming(tweak);
    } else {
      setSelected(toggle(selected, tweak, tweaks ?? []));
    }
  }

  const load = useCallback(() => {
    setError(null);
    bridge.scan().then(setTweaks, (reason: Error) => setError(reason.message));
  }, []);

  useEffect(load, [load]);

  const inPage = useMemo(
    () => (tweaks ?? []).filter((tweak) => categories.includes(tweak.category) && tweak.state !== "unsupported"),
    [tweaks, categories],
  );
  const visible = useMemo(() => inPage.filter((tweak) => matchesSearch(tweak, query)), [inPage, query]);
  const locked = useMemo(() => visible.filter((tweak) => tweak.locked), [visible]);

  if (error) {
    return (
      <p role="alert" className="font-medium text-destructive">
        {error}
      </p>
    );
  }

  if (!tweaks) {
    return <p className="text-muted-foreground">Reading your current settings.</p>;
  }

  return (
    <div className="pb-24">
      <div className="flex flex-wrap items-center gap-3">
        <label className="relative min-w-64 flex-1">
          <span className="sr-only">Search tweaks</span>
          <Search aria-hidden="true" className="absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground" />
          <input
            type="search"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="Search"
            className="min-h-11 w-full rounded-md border border-border bg-card pr-3 pl-10"
          />
        </label>
        {showPresets && (
          <div role="group" aria-label="Presets" className="flex gap-0.5 rounded-md bg-muted p-[3px]">
            {presetOrder.map((preset) => (
              <button
                key={preset}
                type="button"
                onClick={() => setSelected(presetSelection(preset, inPageOrAll(tweaks, categories)))}
                className="min-h-8 cursor-pointer rounded-md px-3.5 font-medium text-muted-foreground hover:bg-card hover:text-foreground"
              >
                {presetLabels[preset]}
              </button>
            ))}
            {profileChoices.map((profile) => (
              <button
                key={profile.id}
                type="button"
                title={`Pick changes suited to ${profile.label.toLowerCase()}`}
                onClick={() => setSelected(new Set(profileIds([profile.id], tweaks)))}
                className="min-h-8 cursor-pointer rounded-md px-3.5 font-medium text-muted-foreground hover:bg-card hover:text-foreground"
              >
                {profile.label}
              </button>
            ))}
          </div>
        )}
      </div>

      <LockedGroup items={locked} onChanged={load} />

      {categories
        .map((category) => ({ category, items: visible.filter((tweak) => tweak.category === category && !tweak.locked) }))
        .filter((section) => section.items.length > 0)
        .map((section) => (
          <section key={section.category} className="mt-6">
            <h2 className="font-semibold text-muted-foreground">{categoryLabels[section.category]}</h2>
            {categoryNotes[section.category] && (
              <p className="mt-1 text-sm text-warning">{categoryNotes[section.category]}</p>
            )}
            <div className="tweak-list mt-2 flex flex-col gap-1.5">
              {section.items.filter((tweak) => !tweak.group).map((tweak) => (
                <TweakRow
                  key={tweak.id}
                  tweak={tweak}
                  checked={selected.has(tweak.id)}
                  onToggle={() => change(tweak)}
                />
              ))}
              {[...new Set(section.items.flatMap((tweak) => (tweak.group ? [tweak.group] : [])))].map((group) => (
                <ChoiceGroup
                  key={group}
                  group={group}
                  options={section.items.filter((tweak) => tweak.group === group)}
                  selected={selected}
                  onChange={(tweak) => setSelected(toggle(selected, tweak, tweaks))}
                />
              ))}
            </div>
          </section>
        ))}

      {visible.length === 0 && <p className="mt-6 text-muted-foreground">No tweaks match your search.</p>}

      {selected.size > 0 && (
        <div className="fixed right-8 bottom-6 z-10 flex items-center gap-3 rounded-lg border border-track bg-card py-2.5 pr-3 pl-5 shadow-2xl shadow-black/60">
          <p>{selected.size} selected</p>
          <button
            type="button"
            onClick={() => setSelected(new Set())}
            className="min-h-11 cursor-pointer rounded-md border border-border px-4 font-medium"
          >
            Clear
          </button>
          <button
            type="button"
            onClick={() => setReviewing([...selected])}
            className="min-h-11 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary"
          >
            Review changes
          </button>
        </div>
      )}

      {confirming && (
        <HazardDialog
          title={confirming.title}
          description={confirming.description}
          onCancel={() => setConfirming(null)}
          onConfirm={() => {
            setSelected(toggle(selected, confirming, tweaks));
            setConfirming(null);
          }}
        />
      )}

      {reviewing && (
        <ReviewDialog
          ids={reviewing}
          onClose={(changed) => {
            setReviewing(null);
            if (changed) {
              setSelected(new Set());
              load();
            }
          }}
        />
      )}
    </div>
  );
}

/** Settings another tool set as a Windows policy. History has no record of them, so this is the way back. */
function LockedGroup({ items, onChanged }: { items: Tweak[]; onChanged: () => void }) {
  const [confirming, setConfirming] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<OperationResult | string | null>(null);

  async function remove(tweak: Tweak) {
    setConfirming(null);
    setBusy(tweak.id);
    setResult(null);
    try {
      setResult(await bridge.removeLock(tweak.id));
      onChanged();
    } catch (reason) {
      setResult((reason as Error).message);
    } finally {
      setBusy(null);
    }
  }

  const failed = typeof result === "string" || result?.ok === false;
  const text = typeof result === "string" ? result : result?.message;
  const status = <div role="status">{text && <p className={`mt-6 font-medium ${failed ? "text-destructive" : "text-success"}`}>{text}</p>}</div>;
  if (items.length === 0) {
    return status;
  }

  return (
    <section aria-labelledby="locked-title" className="mt-6">
      {status}
      <div className="mb-2 flex flex-wrap items-baseline justify-between gap-x-4">
        <h2 id="locked-title" className="font-semibold text-muted-foreground">
          Locked by another app ({items.length})
        </h2>
        <p className="text-sm text-muted-foreground">Windows shows these as managed by your organization</p>
      </div>
      <ul className="flex flex-col gap-1.5">
        {items.map((tweak) => (
          <li key={tweak.id} className="rounded-lg border border-dashed border-border px-4 py-2.5">
            <div className="flex items-center gap-3.5">
              <Lock aria-hidden="true" className="size-5 shrink-0 text-warning" />
              <div className="min-w-0 flex-1">
                <p className="font-medium">{tweak.title}</p>
                {tweak.description && <p className="text-sm text-muted-foreground">{tweak.description}</p>}
              </div>
              <button
                type="button"
                disabled={busy !== null}
                aria-busy={busy === tweak.id}
                aria-label={`Remove lock on ${tweak.title}`}
                onClick={() => setConfirming(tweak.id)}
                className="min-h-11 shrink-0 cursor-pointer rounded-md border border-border px-4 font-medium disabled:cursor-wait disabled:opacity-60"
              >
                {busy === tweak.id ? "Working" : "Remove lock"}
              </button>
            </div>
            {confirming === tweak.id && (
              <div className="mt-2 flex flex-wrap items-center gap-2">
                <span className="font-medium">Remove this lock? Windows will ask for permission, and you can undo it from History.</span>
                <button type="button" onClick={() => remove(tweak)} className="min-h-11 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary">
                  Yes, remove it
                </button>
                <button type="button" onClick={() => setConfirming(null)} className="min-h-11 cursor-pointer rounded-md border border-border px-4 font-medium">
                  Cancel
                </button>
              </div>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}

function inPageOrAll(tweaks: Tweak[], categories: string[]): Tweak[] {
  return tweaks.filter((tweak) => categories.includes(tweak.category));
}

function Badges({ tweak }: { tweak: Tweak }) {
  return (
    <>
      {tweak.state === "applied" && <span className="text-sm text-success">Applied</span>}
      {tweak.risk === "medium" && <span className="text-sm text-warning">Medium risk</span>}
      {tweak.hazard ? (
        <span className="text-sm font-semibold text-destructive">{hazardLabel}</span>
      ) : (
        tweak.risk === "high" && <span className="text-sm font-medium text-destructive">High risk</span>
      )}
    </>
  );
}

function TweakRow({ tweak, checked, onToggle }: { tweak: Tweak; checked: boolean; onToggle: () => void }) {
  const applied = tweak.state === "applied";
  const Icon = categoryIcons[tweak.category] ?? SlidersHorizontal;
  return (
    <label
      className={`flex min-h-14 gap-3 rounded-lg border border-border bg-card px-4 py-2.5 ${applied ? "" : "cursor-pointer"}`}
    >
      <input type="checkbox" checked={checked || applied} disabled={applied} onChange={onToggle} />
      <span className="flex min-w-0 items-center gap-3.5">
        <Icon aria-hidden="true" className="size-5 shrink-0 text-muted-foreground" />
        <span className="min-w-0">
          <span className="flex flex-wrap items-center gap-2">
            <span className="font-medium">{tweak.title}</span>
            <Badges tweak={tweak} />
          </span>
          {tweak.description && <span className="block text-sm text-muted-foreground">{tweak.description}</span>}
          {tweak.impact && <span className="block text-sm text-warning">{tweak.impact}</span>}
        </span>
      </span>
    </label>
  );
}

interface ChoiceGroupProps {
  group: string;
  options: Tweak[];
  selected: ReadonlySet<string>;
  onChange: (tweak: Tweak) => void;
}

function ChoiceGroup({ group, options, selected, onChange }: ChoiceGroupProps) {
  return (
    <fieldset className="rounded-lg border border-border bg-card px-4 pt-1 pb-2">
      <legend className="px-1 font-medium">{groupLabels[group] ?? group}</legend>
      {options.map((tweak) => {
        const applied = tweak.state === "applied";
        return (
          <label key={tweak.id} className={`flex min-h-11 items-center gap-3 ${applied ? "" : "cursor-pointer"}`}>
            <input
              type="checkbox"
              checked={selected.has(tweak.id) || applied}
              disabled={applied}
              onChange={() => onChange(tweak)}
            />
            <span>
              <span className="flex flex-wrap items-center gap-2">
                {tweak.title}
                <Badges tweak={tweak} />
              </span>
              {tweak.description && <span className="block text-sm text-muted-foreground">{tweak.description}</span>}
              {tweak.impact && <span className="block text-sm text-warning">{tweak.impact}</span>}
            </span>
          </label>
        );
      })}
    </fieldset>
  );
}
