import { CircleAlert, CircleCheck, CircleHelp, CircleX, type LucideIcon } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { bridge, type HealthCheck, type HealthReport, type LiveStats, type SystemSummary } from "./bridge";
import { formatBytes } from "./format";

const LIVE_INTERVAL_MS = 2000;

const statusStyles: Record<HealthCheck["status"], { icon: LucideIcon; label: string; color: string }> = {
  good: { icon: CircleCheck, label: "Good", color: "text-success" },
  warn: { icon: CircleAlert, label: "Check this", color: "text-warning" },
  bad: { icon: CircleX, label: "Needs attention", color: "text-destructive" },
  unknown: { icon: CircleHelp, label: "Unknown", color: "text-muted-foreground" },
};

/** A headline number, with a meter only when the value is a share of something. */
function StatTile({ label, value, detail, percent }: { label: string; value: string; detail?: string; percent?: number | null }) {
  return (
    <div className="rounded-md border border-border bg-card p-4">
      <p className="text-sm text-muted-foreground">{label}</p>
      <p className="mt-1 text-2xl font-semibold tabular-nums">{value}</p>
      {percent !== undefined && percent !== null && (
        <div
          role="meter"
          aria-label={label}
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={Math.round(percent)}
          className="mt-2 h-2 overflow-hidden rounded-full bg-muted"
        >
          <div
            className="h-full rounded-full bg-primary transition-[width] duration-300"
            style={{ width: `${Math.min(100, percent)}%` }}
          />
        </div>
      )}
      {detail && <p className="mt-2 text-sm text-muted-foreground">{detail}</p>}
    </div>
  );
}

function LiveTiles() {
  const [stats, setStats] = useState<LiveStats | null>(null);

  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;

    // Each read waits for the last, so a slow PC can't pile up requests.
    async function read() {
      try {
        const next = await bridge.monitorLive();
        if (!stopped) {
          setStats(next);
        }
      } catch {
        // The next read tries again.
      }
      if (!stopped) {
        timer = setTimeout(read, LIVE_INTERVAL_MS);
      }
    }

    void read();
    return () => {
      stopped = true;
      clearTimeout(timer);
    };
  }, []);

  if (!stats) {
    return <p className="text-muted-foreground">Reading live usage.</p>;
  }

  const memory = stats.memoryTotalBytes ? (100 * stats.memoryUsedBytes) / stats.memoryTotalBytes : 0;
  const gpu = stats.gpuPercent;
  return (
    <div className="grid grid-cols-2 gap-3 lg:grid-cols-3">
      <StatTile label="Processor" value={`${Math.round(stats.cpuPercent)}%`} percent={stats.cpuPercent} />
      <StatTile
        label="Memory"
        value={`${Math.round(memory)}%`}
        percent={memory}
        detail={`${formatBytes(stats.memoryUsedBytes)} of ${formatBytes(stats.memoryTotalBytes)}`}
      />
      <StatTile
        label="Disk"
        value={stats.diskPercent == null ? "Not available" : `${Math.round(stats.diskPercent)}%`}
        percent={stats.diskPercent}
      />
      <StatTile
        label="Graphics"
        value={gpu == null ? "Not available" : `${Math.round(gpu)}%`}
        percent={gpu}
        detail={stats.gpuTemperature == null ? undefined : `${Math.round(stats.gpuTemperature)} °C`}
      />
      <StatTile label="Download" value={`${formatBytes(stats.networkDownPerSecond)}/s`} />
      <StatTile label="Upload" value={`${formatBytes(stats.networkUpPerSecond)}/s`} />
    </div>
  );
}

function HealthCard() {
  const [report, setReport] = useState<HealthReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState<string | null>(null);

  const check = useCallback(() => {
    setReport(null);
    setError(null);
    bridge.healthCheck().then(setReport, (reason: Error) => setError(reason.message));
  }, []);
  useEffect(check, [check]);

  async function save() {
    setSaving(true);
    setSaved(null);
    try {
      const result = await bridge.reportSave();
      if (result.saved) {
        setSaved(`Saved to ${result.path}`);
      }
    } catch (reason) {
      setError((reason as Error).message);
    } finally {
      setSaving(false);
    }
  }

  return (
    <section aria-labelledby="health-title" className="rounded-lg border border-border bg-card p-6">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 id="health-title" className="text-lg font-semibold">
            PC health
          </h2>
          {report ? (
            <p className="mt-1 text-4xl font-semibold tabular-nums">
              {report.score}
              <span className="text-lg font-normal text-muted-foreground"> out of 100</span>
            </p>
          ) : (
            !error && <p className="mt-1 text-muted-foreground">Checking this PC.</p>
          )}
        </div>
        <div className="flex gap-2">
          <button
            type="button"
            onClick={check}
            disabled={!report && !error}
            className="min-h-11 cursor-pointer rounded-md border border-border px-4 font-medium transition-colors duration-200 hover:bg-muted disabled:cursor-wait disabled:opacity-60"
          >
            Check again
          </button>
          <button
            type="button"
            onClick={save}
            disabled={saving}
            className="min-h-11 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary disabled:cursor-wait disabled:opacity-60"
          >
            {saving ? "Preparing" : "Save report"}
          </button>
        </div>
      </div>

      <div role="status">
        {saved && <p className="mt-3 font-medium text-success">{saved}</p>}
        {error && <p className="mt-3 font-medium text-destructive">{error}</p>}
      </div>

      {report && (
        <ul className="mt-4 flex flex-col gap-3">
          {report.checks.map((item) => {
            const style = statusStyles[item.status];
            const Icon = style.icon;
            return (
              <li key={item.id} className="flex gap-3">
                <Icon aria-hidden="true" className={`mt-0.5 size-5 shrink-0 ${style.color}`} />
                <div>
                  <p className="font-medium">
                    {item.title}
                    <span className="sr-only">: {style.label}</span>
                  </p>
                  <p className="text-sm text-muted-foreground">{item.detail}</p>
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

function formatUptime(seconds: number) {
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  return days > 0 ? `${days} days, ${hours} hours` : `${hours} hours`;
}

function HardwareCard() {
  const [system, setSystem] = useState<SystemSummary | null>(null);

  useEffect(() => {
    bridge.systemSummary().then(setSystem, () => setSystem(null));
  }, []);

  if (!system) {
    return null;
  }

  const rows: [string, string][] = [
    ["Windows", `${system.windows} (build ${system.build})`],
    ["Processor", `${system.cpu}, ${system.cores} cores, ${system.threads} threads`],
    ["Graphics", system.gpus.join(", ")],
    ["Memory", formatBytes(system.memoryBytes)],
    ["Motherboard", system.board],
    ["Running for", formatUptime(system.uptimeSeconds)],
  ];

  return (
    <section aria-labelledby="hardware-title" className="rounded-lg border border-border bg-card p-6">
      <h2 id="hardware-title" className="text-lg font-semibold">
        {system.computer}
      </h2>
      <dl className="mt-3 grid grid-cols-[auto_1fr] gap-x-6 gap-y-1">
        {rows.map(([label, value]) => (
          <div key={label} className="contents">
            <dt className="text-muted-foreground">{label}</dt>
            <dd className="min-w-0">{value}</dd>
          </div>
        ))}
      </dl>
    </section>
  );
}

export function DashboardPage() {
  return (
    <div className="flex flex-col gap-6">
      <HealthCard />
      <LiveTiles />
      <HardwareCard />
    </div>
  );
}
