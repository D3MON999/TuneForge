import { useCallback, useEffect, useMemo, useState } from "react";
import { bridge, type BuiltInApp, type InstalledProgram, type OperationResult, type SoftwareEntry } from "./bridge";
import { matchesText } from "./selection";

type Outcome = OperationResult | string | null;

const buttonClass =
  "min-h-11 shrink-0 cursor-pointer rounded-md border border-border px-4 font-medium disabled:cursor-not-allowed disabled:opacity-60";
const primaryClass =
  "min-h-11 shrink-0 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary disabled:cursor-not-allowed disabled:opacity-60";
const fieldClass = "min-h-11 rounded-md border border-border bg-card px-3";

function Notice({ result }: { result: Outcome }) {
  const text = typeof result === "string" ? result : result?.message;
  const failed = typeof result === "string" || result?.ok === false;
  return (
    <div role="status">
      {text && <p className={`mb-3 font-medium ${failed ? "text-destructive" : "text-success"}`}>{text}</p>}
    </div>
  );
}

function WingetMissing() {
  return (
    <p className="text-muted-foreground">
      winget isn't on this PC. Install App Installer from the Microsoft Store, then come back here.
    </p>
  );
}

const messageOf = (reason: unknown) => (reason as Error).message;

export function BuiltInAppsTab() {
  const [apps, setApps] = useState<BuiltInApp[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<Outcome>(null);

  const reload = useCallback(() => {
    bridge.builtInList().then(setApps, (reason: Error) => setError(reason.message));
  }, []);
  useEffect(reload, [reload]);

  const shown = useMemo(
    () => (apps ?? []).filter((app) => matchesText(`${app.title} ${app.note} ${app.id}`, query)),
    [apps, query],
  );
  const installed = shown.filter((app) => app.installed);
  const groups = [...new Set(installed.map((app) => app.group))].sort();
  const removed = shown.filter((app) => !app.installed);
  const chosen = (apps ?? []).filter((app) => selected.has(app.id));
  const risky = chosen.filter((app) => app.safety === "caution");

  function toggle(id: string) {
    const next = new Set(selected);
    if (!next.delete(id)) {
      next.add(id);
    }
    setSelected(next);
    setConfirming(false);
  }

  function selectRecommended() {
    setSelected(new Set((apps ?? []).filter((app) => app.installed && app.safety === "recommended").map((app) => app.id)));
    setConfirming(false);
  }

  async function remove() {
    setConfirming(false);
    setBusy("remove");
    try {
      const outcome = await bridge.builtInRemove([...selected]);
      const titles = (ids: string[]) =>
        ids.map((id) => apps?.find((app) => app.id === id)?.title ?? id).join(", ");
      setResult({
        ok: outcome.failed.length === 0,
        message:
          outcome.failed.length === 0
            ? `Removed ${outcome.removed.length} apps.`
            : `Removed ${outcome.removed.length} apps. Windows wouldn't remove: ${titles(outcome.failed)}.`,
        restartNeeded: false,
      });
      setSelected(new Set());
      reload();
    } catch (reason) {
      setResult(messageOf(reason));
    } finally {
      setBusy(null);
    }
  }

  async function reinstall(app: BuiltInApp) {
    setBusy(app.id);
    try {
      setResult(await bridge.builtInReinstall(app.id));
      reload();
    } catch (reason) {
      setResult(messageOf(reason));
    } finally {
      setBusy(null);
    }
  }

  if (error) {
    return <p role="alert" className="font-medium text-destructive">{error}</p>;
  }

  return (
    <div>
      <Notice result={result} />
      <p className="mb-3 text-muted-foreground">
        Removal applies to every account on this PC and can't be undone from History. Only some apps can be reinstalled here.
      </p>
      <div className="mb-3 flex flex-wrap gap-2">
        <input
          type="search"
          aria-label="Search built-in apps"
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          placeholder="Search"
          className={`${fieldClass} min-w-0 flex-1`}
        />
        <button type="button" disabled={busy !== null || !apps} onClick={selectRecommended} className={buttonClass}>
          Select recommended
        </button>
        <button
          type="button"
          disabled={busy !== null || selected.size === 0}
          aria-busy={busy === "remove"}
          onClick={() => setConfirming(true)}
          className={primaryClass}
        >
          {busy === "remove" ? "Removing" : `Remove ${selected.size} apps`}
        </button>
      </div>

      {confirming && (
        <div className="mb-3 rounded-md border border-border bg-card p-4">
          <p className="font-medium">Remove {chosen.length} apps from every account on this PC?</p>
          {risky.length > 0 && (
            <p className="mt-1 text-destructive">
              These can break Windows features: {risky.map((app) => app.title).join(", ")}.
            </p>
          )}
          <div className="mt-3 flex gap-2">
            <button type="button" onClick={remove} className={primaryClass}>
              Remove apps
            </button>
            <button type="button" onClick={() => setConfirming(false)} className={buttonClass}>
              Cancel
            </button>
          </div>
        </div>
      )}

      {!apps && <p className="text-muted-foreground">Reading installed apps.</p>}

      {groups.map((group) => (
        <section key={group} className="mb-4">
          <h3 className="mb-2 font-semibold">{group}</h3>
          <div className="flex flex-col gap-2">
            {installed
              .filter((app) => app.group === group)
              .map((app) => (
                <label
                  key={app.id}
                  className="flex min-h-11 cursor-pointer items-center gap-3 rounded-md border border-border bg-card p-3"
                >
                  <input
                    type="checkbox"
                    checked={selected.has(app.id)}
                    disabled={busy !== null}
                    onChange={() => toggle(app.id)}
                    className="size-5 shrink-0"
                  />
                  <span className="min-w-0">
                    <span className="font-medium">{app.title}</span>
                    {app.safety === "caution" && (
                      <span className="ml-2 text-sm font-medium text-destructive">Can break things</span>
                    )}
                    <span className="block text-sm text-muted-foreground">{app.note}</span>
                  </span>
                </label>
              ))}
          </div>
        </section>
      ))}

      {removed.length > 0 && (
        <section>
          <h3 className="mb-2 font-semibold">Not installed</h3>
          <div className="flex flex-col gap-2">
            {removed.map((app) => (
              <div key={app.id} className="flex items-center justify-between gap-3 rounded-md border border-border bg-card p-3">
                <div className="min-w-0">
                  <p className="font-medium">{app.title}</p>
                  <p className="text-sm text-muted-foreground">{app.note}</p>
                </div>
                <button
                  type="button"
                  disabled={busy !== null}
                  aria-busy={busy === app.id}
                  aria-label={`Reinstall ${app.title}`}
                  onClick={() => reinstall(app)}
                  className={buttonClass}
                >
                  {busy === app.id ? "Installing" : "Reinstall"}
                </button>
              </div>
            ))}
          </div>
        </section>
      )}
    </div>
  );
}

export function InstallAppsTab() {
  const [items, setItems] = useState<SoftwareEntry[] | null>(null);
  const [available, setAvailable] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [category, setCategory] = useState("");
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [progress, setProgress] = useState<string | null>(null);
  const [results, setResults] = useState<OperationResult[]>([]);

  const reload = useCallback(() => {
    bridge.softwareList().then((list) => {
      setAvailable(list.wingetAvailable);
      setItems(list.items);
    }, (reason: Error) => setError(reason.message));
  }, []);
  useEffect(reload, [reload]);

  const categories = useMemo(() => [...new Set((items ?? []).map((item) => item.category))].sort(), [items]);
  const shown = useMemo(
    () =>
      (items ?? []).filter(
        (item) => (!category || item.category === category) && matchesText(`${item.title} ${item.note}`, query),
      ),
    [items, category, query],
  );

  function toggle(id: string) {
    const next = new Set(selected);
    if (!next.delete(id)) {
      next.add(id);
    }
    setSelected(next);
  }

  async function install() {
    const queue = (items ?? []).filter((item) => selected.has(item.id));
    const done: OperationResult[] = [];
    setResults([]);
    for (const [index, item] of queue.entries()) {
      setProgress(`Installing ${index + 1} of ${queue.length}: ${item.title}`);
      try {
        done.push(await bridge.softwareInstall(item.id));
      } catch (reason) {
        done.push({ ok: false, message: `${item.title}: ${messageOf(reason)}`, restartNeeded: false });
      }
      setResults([...done]);
    }
    setProgress(null);
    setSelected(new Set());
    reload();
  }

  if (error) {
    return <p role="alert" className="font-medium text-destructive">{error}</p>;
  }
  if (!available) {
    return <WingetMissing />;
  }

  return (
    <div>
      <div role="status">{progress && <p className="mb-3 font-medium">{progress}</p>}</div>
      {results.map((result, index) => (
        <Notice key={index} result={result} />
      ))}
      <p className="mb-3 text-muted-foreground">
        Apps download from winget. Installing means you accept each app's license and winget's source terms. Some installers show their own Windows prompt.
      </p>
      <div className="mb-3 flex flex-wrap gap-2">
        <input
          type="search"
          aria-label="Search apps to install"
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          placeholder="Search"
          className={`${fieldClass} min-w-0 flex-1`}
        />
        <select
          aria-label="Category"
          value={category}
          onChange={(event) => setCategory(event.target.value)}
          className={`${fieldClass} cursor-pointer`}
        >
          <option value="">All categories</option>
          {categories.map((name) => (
            <option key={name} value={name}>
              {name}
            </option>
          ))}
        </select>
        <button type="button" disabled={progress !== null || selected.size === 0} onClick={install} className={primaryClass}>
          Install {selected.size} apps
        </button>
      </div>
      {!items && <p className="text-muted-foreground">Checking what's installed. This can take a minute.</p>}
      <div className="flex flex-col gap-2">
        {shown.map((item) => (
          <label
            key={item.id}
            className="flex min-h-11 cursor-pointer items-center gap-3 rounded-md border border-border bg-card p-3"
          >
            <input
              type="checkbox"
              checked={selected.has(item.id)}
              disabled={progress !== null || item.installed}
              onChange={() => toggle(item.id)}
              className="size-5 shrink-0"
            />
            <span className="min-w-0">
              <span className="font-medium">{item.title}</span>
              {item.installed && <span className="ml-2 text-sm text-muted-foreground">Installed</span>}
              <span className="block text-sm text-muted-foreground">{item.note}</span>
            </span>
          </label>
        ))}
      </div>
    </div>
  );
}

export function InstalledAppsTab() {
  const [items, setItems] = useState<InstalledProgram[] | null>(null);
  const [available, setAvailable] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<string | null>(null);
  const [progress, setProgress] = useState<string | null>(null);
  const [results, setResults] = useState<OperationResult[]>([]);

  const reload = useCallback(() => {
    bridge.programsList().then((list) => {
      setAvailable(list.wingetAvailable);
      setItems(list.items);
    }, (reason: Error) => setError(reason.message));
  }, []);
  useEffect(reload, [reload]);

  const shown = useMemo(
    () => (items ?? []).filter((item) => matchesText(`${item.name} ${item.id}`, query)),
    [items, query],
  );
  const updates = (items ?? []).filter((item) => item.available);
  const working = busy !== null || progress !== null;

  async function run(program: InstalledProgram, action: (id: string) => Promise<OperationResult>) {
    setBusy(program.id);
    setConfirm(null);
    setResults([]);
    try {
      setResults([await action(program.id)]);
      reload();
    } catch (reason) {
      setResults([{ ok: false, message: messageOf(reason), restartNeeded: false }]);
    } finally {
      setBusy(null);
    }
  }

  async function updateAll() {
    const done: OperationResult[] = [];
    setResults([]);
    for (const [index, program] of updates.entries()) {
      setProgress(`Updating ${index + 1} of ${updates.length}: ${program.name}`);
      try {
        done.push(await bridge.programsUpdate(program.id));
      } catch (reason) {
        done.push({ ok: false, message: `${program.name}: ${messageOf(reason)}`, restartNeeded: false });
      }
      setResults([...done]);
    }
    setProgress(null);
    reload();
  }

  if (error) {
    return <p role="alert" className="font-medium text-destructive">{error}</p>;
  }
  if (!available) {
    return <WingetMissing />;
  }

  return (
    <div>
      <div role="status">{progress && <p className="mb-3 font-medium">{progress}</p>}</div>
      {results.map((result, index) => (
        <Notice key={index} result={result} />
      ))}
      <div className="mb-3 flex flex-wrap gap-2">
        <input
          type="search"
          aria-label="Search installed apps"
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          placeholder="Search"
          className={`${fieldClass} min-w-0 flex-1`}
        />
        <button type="button" disabled={working || updates.length === 0} onClick={updateAll} className={primaryClass}>
          Update all ({updates.length})
        </button>
      </div>
      {!items && <p className="text-muted-foreground">Checking installed apps and updates. This can take a minute.</p>}
      <div className="flex flex-col gap-2">
        {shown.map((program) => (
          <div key={program.id} className="rounded-md border border-border bg-card p-3">
            <div className="flex items-center justify-between gap-3">
              <div className="min-w-0">
                <p className="font-medium">{program.name}</p>
                <p className="truncate text-sm text-muted-foreground">
                  {program.version}
                  {program.available ? ` to ${program.available}` : ""}
                </p>
              </div>
              <div className="flex shrink-0 items-center gap-2">
                {program.available && (
                  <button
                    type="button"
                    disabled={working}
                    aria-busy={busy === program.id}
                    aria-label={`Update ${program.name}`}
                    onClick={() => run(program, bridge.programsUpdate)}
                    className={buttonClass}
                  >
                    Update
                  </button>
                )}
                {program.locked ? (
                  <span className="text-sm text-muted-foreground">Locked</span>
                ) : (
                  <button
                    type="button"
                    disabled={working}
                    aria-label={`Uninstall ${program.name}`}
                    onClick={() => setConfirm(program.id)}
                    className={buttonClass}
                  >
                    Uninstall
                  </button>
                )}
              </div>
            </div>
            {confirm === program.id && (
              <div className="mt-3 flex flex-wrap items-center gap-2">
                <span className="font-medium">Uninstall {program.name}?</span>
                <button type="button" onClick={() => run(program, bridge.programsUninstall)} className={primaryClass}>
                  Yes, uninstall
                </button>
                <button type="button" onClick={() => setConfirm(null)} className={buttonClass}>
                  Cancel
                </button>
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}
