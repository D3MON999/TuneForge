import { useCallback, useEffect, useMemo, useState } from "react";
import { bridge, type FeatureEntry, type OperationResult, type ServiceEntry, type StartupEntry } from "./bridge";
import { matchesText } from "./selection";

type Tab = "startup" | "services" | "features";

const tabs: { id: Tab; label: string }[] = [
  { id: "startup", label: "Startup" },
  { id: "services", label: "Services" },
  { id: "features", label: "Windows features" },
];

export function AppsPage() {
  const [tab, setTab] = useState<Tab>("startup");

  return (
    <div>
      <div role="tablist" aria-label="Apps sections" className="flex gap-2">
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
        {tab === "startup" && <StartupTab />}
        {tab === "services" && <ServicesTab />}
        {tab === "features" && <FeaturesTab />}
      </div>
    </div>
  );
}

/** Shows the last result, so every change says what happened. */
function Notice({ result }: { result: OperationResult | string | null }) {
  const text = typeof result === "string" ? result : result?.message;
  const failed = typeof result === "string" || result?.ok === false;
  return (
    <div role="status">
      {text && <p className={`mb-3 font-medium ${failed ? "text-destructive" : "text-success"}`}>{text}</p>}
    </div>
  );
}

function useLoader<T>(load: () => Promise<T[]>) {
  const [items, setItems] = useState<T[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const reload = useCallback(() => {
    load().then(setItems, (reason: Error) => setError(reason.message));
  }, [load]);

  useEffect(reload, [reload]);
  return { items, error, reload };
}

function StartupTab() {
  const { items, error, reload } = useLoader(bridge.startupList);
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<OperationResult | string | null>(error);

  async function change(entry: StartupEntry) {
    setBusy(entry.source + entry.name);
    try {
      setResult(await bridge.startupSet(entry.source, entry.name, !entry.enabled));
      reload();
    } catch (reason) {
      setResult((reason as Error).message);
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
      {!items && <p className="text-muted-foreground">Reading startup programs.</p>}
      {items?.length === 0 && <p className="text-muted-foreground">No startup programs were found.</p>}
      <div className="flex flex-col gap-2">
        {items?.map((entry) => (
          <label
            key={entry.source + entry.name}
            className="flex min-h-11 cursor-pointer items-center gap-3 rounded-md border border-border bg-card p-3"
          >
            <input
              type="checkbox"
              checked={entry.enabled}
              disabled={busy !== null}
              onChange={() => change(entry)}
              className="size-5 shrink-0"
            />
            <span className="min-w-0">
              <span className="font-medium">{entry.name}</span>
              <span className="block truncate text-sm text-muted-foreground">{entry.command}</span>
            </span>
          </label>
        ))}
      </div>
    </div>
  );
}

const startLabels: Record<string, string> = {
  auto: "Automatic",
  manual: "Manual",
  disabled: "Disabled",
};

function ServicesTab() {
  const { items, error, reload } = useLoader(bridge.serviceList);
  const [query, setQuery] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<OperationResult | string | null>(null);

  const visible = useMemo(
    () => (items ?? []).filter((service) => matchesText(`${service.displayName} ${service.name}`, query)),
    [items, query],
  );

  async function change(service: ServiceEntry, startType: string) {
    setBusy(true);
    try {
      setResult(await bridge.serviceSet(service.name, startType));
      reload();
    } catch (reason) {
      setResult((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  if (error) {
    return <p role="alert" className="font-medium text-destructive">{error}</p>;
  }

  return (
    <div>
      <Notice result={result} />
      <p className="mb-3 text-muted-foreground">
        Changing services can break Windows features. Disabled services stay off until you change them back. Essential and security services are locked.
      </p>
      <input
        type="search"
        aria-label="Search services"
        value={query}
        onChange={(event) => setQuery(event.target.value)}
        placeholder="Search"
        className="mb-3 min-h-11 w-full rounded-md border border-border bg-card px-3"
      />
      {!items && <p className="text-muted-foreground">Reading services.</p>}
      <div className="flex flex-col gap-2">
        {visible.map((service) => (
          <div key={service.name} className="flex items-center justify-between gap-3 rounded-md border border-border bg-card p-3">
            <div className="min-w-0">
              <p className="font-medium">{service.displayName}</p>
              <p className="text-sm text-muted-foreground">
                {service.name}, {service.running ? "running" : "stopped"}
              </p>
            </div>
            {service.protected ? (
              <span className="shrink-0 text-sm text-muted-foreground">Locked</span>
            ) : (
              <select
                aria-label={`Startup type for ${service.displayName}`}
                value={service.startType}
                disabled={busy}
                onChange={(event) => change(service, event.target.value)}
                className="min-h-11 shrink-0 cursor-pointer rounded-md border border-border bg-background px-3"
              >
                {Object.entries(startLabels).map(([value, label]) => (
                  <option key={value} value={value}>
                    {label}
                  </option>
                ))}
              </select>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}

function FeaturesTab() {
  const { items, error, reload } = useLoader(bridge.featureList);
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<OperationResult | string | null>(null);

  async function change(feature: FeatureEntry) {
    setBusy(feature.id);
    try {
      setResult(await bridge.featureSet(feature.id, feature.state !== "enabled"));
      reload();
    } catch (reason) {
      setResult((reason as Error).message);
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
      {!items && <p className="text-muted-foreground">Reading Windows features.</p>}
      <div className="flex flex-col gap-2">
        {items?.map((feature) => (
          <div key={feature.id} className="flex items-center justify-between gap-3 rounded-md border border-border bg-card p-3">
            <div>
              <p className="font-medium">{feature.title}</p>
              <p className="text-sm text-muted-foreground">{feature.note}</p>
            </div>
            {feature.state === "unavailable" ? (
              <span className="shrink-0 text-sm text-muted-foreground">Not available</span>
            ) : (
              <button
                type="button"
                disabled={busy !== null}
                aria-busy={busy === feature.id}
                aria-label={`${feature.state === "enabled" ? "Turn off" : "Turn on"} ${feature.title}`}
                onClick={() => change(feature)}
                className="min-h-11 shrink-0 cursor-pointer rounded-md border border-border px-4 font-medium disabled:cursor-wait disabled:opacity-60"
              >
                {busy === feature.id ? "Working" : feature.state === "enabled" ? "Turn off" : "Turn on"}
              </button>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}
