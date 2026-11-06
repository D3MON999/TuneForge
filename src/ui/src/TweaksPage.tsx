import { Search } from "lucide-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { bridge, type Tweak } from "./bridge";
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

export function TweaksPage({ categories, showPresets = false }: { categories: string[]; showPresets?: boolean }) {
  const [tweaks, setTweaks] = useState<Tweak[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [query, setQuery] = useState("");
  const [reviewing, setReviewing] = useState<string[] | null>(null);

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
          <Search aria-hidden="true" className="absolute top-3 left-3 size-5 text-muted-foreground" />
          <input
            type="search"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="Search"
            className="min-h-11 w-full rounded-md border border-border bg-card pr-3 pl-10"
          />
        </label>
        {showPresets && (
          <div role="group" aria-label="Presets" className="flex gap-2">
            {presetOrder.map((preset) => (
              <button
                key={preset}
                type="button"
                onClick={() => setSelected(presetSelection(preset, inPageOrAll(tweaks, categories)))}
                className="min-h-11 cursor-pointer rounded-md border border-border bg-card px-3 font-medium transition-colors duration-200 hover:bg-muted"
              >
                {presetLabels[preset]}
              </button>
            ))}
          </div>
        )}
      </div>

      {categories
        .map((category) => ({ category, items: visible.filter((tweak) => tweak.category === category) }))
        .filter((section) => section.items.length > 0)
        .map((section) => (
          <section key={section.category} className="mt-6">
            <h2 className="text-lg font-semibold">{categoryLabels[section.category]}</h2>
            <div className="mt-2 flex flex-col gap-2">
              {section.items.filter((tweak) => !tweak.group).map((tweak) => (
                <TweakRow
                  key={tweak.id}
                  tweak={tweak}
                  checked={selected.has(tweak.id)}
                  onToggle={() => setSelected(toggle(selected, tweak, tweaks))}
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
        <div className="fixed right-8 bottom-6 flex items-center gap-3 rounded-lg border border-border bg-card p-3 shadow-lg">
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

function inPageOrAll(tweaks: Tweak[], categories: string[]): Tweak[] {
  return tweaks.filter((tweak) => categories.includes(tweak.category));
}

function Badges({ tweak }: { tweak: Tweak }) {
  return (
    <>
      {tweak.state === "applied" && <span className="rounded bg-muted px-2 text-sm">Applied</span>}
      {tweak.risk === "medium" && <span className="rounded border border-border px-2 text-sm">Medium risk</span>}
    </>
  );
}

function TweakRow({ tweak, checked, onToggle }: { tweak: Tweak; checked: boolean; onToggle: () => void }) {
  const applied = tweak.state === "applied";
  return (
    <label
      className={`flex min-h-11 items-start gap-3 rounded-md border border-border bg-card p-3 ${applied ? "" : "cursor-pointer"}`}
    >
      <input type="checkbox" checked={checked} disabled={applied} onChange={onToggle} className="mt-0.5 size-5 shrink-0" />
      <span className="min-w-0">
        <span className="flex flex-wrap items-center gap-2">
          <span className="font-medium">{tweak.title}</span>
          <Badges tweak={tweak} />
        </span>
        {tweak.impact && <span className="block text-sm text-muted-foreground">{tweak.impact}</span>}
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
    <fieldset className="rounded-md border border-border bg-card p-3">
      <legend className="px-1 font-medium">{groupLabels[group] ?? group}</legend>
      {options.map((tweak) => {
        const applied = tweak.state === "applied";
        return (
          <label key={tweak.id} className={`flex min-h-11 items-center gap-3 ${applied ? "" : "cursor-pointer"}`}>
            <input
              type="checkbox"
              checked={selected.has(tweak.id)}
              disabled={applied}
              onChange={() => onChange(tweak)}
              className="size-5 shrink-0"
            />
            <span className="flex flex-wrap items-center gap-2">
              {tweak.title}
              <Badges tweak={tweak} />
            </span>
          </label>
        );
      })}
    </fieldset>
  );
}
