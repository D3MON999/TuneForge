import { useEffect, useState } from "react";
import { bridge, type ErrorGroup, type StorageReport, type WindowsEvent } from "./bridge";
import { GpuCard } from "./ExtraPanels";
import { formatBytes } from "./format";
import { DriverCard, StartupCard } from "./Insights";

type Tab = "storage" | "errors" | "logs";

const tabs: { id: Tab; label: string }[] = [
  { id: "storage", label: "Disks" },
  { id: "errors", label: "Windows errors" },
  { id: "logs", label: "Logs" },
];

export function MonitorPage({ initialTab }: { initialTab?: string }) {
  const [tab, setTab] = useState<Tab>(tabs.some((item) => item.id === initialTab) ? (initialTab as Tab) : "storage");

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

