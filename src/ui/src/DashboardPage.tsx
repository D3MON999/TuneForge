import {
  ChevronRight,
  CircleAlert,
  CircleCheck,
  CircleHelp,
  CircleX,
  Cpu,
  Download,
  Gamepad2,
  Globe,
  HardDrive,
  type LucideIcon,
  MemoryStick,
  MonitorCog,
  Package,
  SlidersHorizontal,
  Sparkles,
  Upload,
  Wrench,
} from "lucide-react";
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

const quickActions: { page: string; icon: LucideIcon; title: string; note: string }[] = [
  { page: "cleanup", icon: Sparkles, title: "Clean up", note: "Clear junk files" },
  { page: "optimize", icon: SlidersHorizontal, title: "Optimize", note: "Tune Windows settings" },
  { page: "gaming", icon: Gamepad2, title: "Boost a game", note: "Set up per-game boosts" },
  { page: "network", icon: Globe, title: "Test network", note: "Check speed and DNS" },
  { page: "apps", icon: Package, title: "Manage apps", note: "Install, update, remove" },
  { page: "repair", icon: Wrench, title: "Repair Windows", note: "Run repair tools" },
];

/** The ring is a picture of the score. The number inside it is what people read. */
function ScoreRing({ score }: { score: number }) {
  const size = 128;
  const stroke = 10;
  const radius = (size - stroke) / 2;
  const length = 2 * Math.PI * radius;
  const middle = size / 2;
  return (
    <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} role="img" aria-label={`Health score ${score} out of 100`} className="shrink-0">
      <circle cx={middle} cy={middle} r={radius} fill="none" stroke="var(--track)" strokeWidth={stroke} />
      <circle
        cx={middle}
        cy={middle}
        r={radius}
        fill="none"
        stroke="var(--primary)"
        strokeWidth={stroke}
        strokeLinecap="round"
        strokeDasharray={length}
        strokeDashoffset={length * (1 - score / 100)}
        transform={`rotate(-90 ${middle} ${middle})`}
      />
      <text x="50%" y="50%" textAnchor="middle" dominantBaseline="central" fontSize="38" fontWeight="600" fill="var(--foreground)">
        {score}
      </text>
    </svg>
  );
}

function headline(score: number) {
  if (score >= 85) {
    return "Your PC is in great shape";
  }

  return score >= 60 ? "Your PC is in fair shape" : "Your PC needs attention";
}

/** A headline number, with a meter only when the value is a share of something. */
function StatTile({
  icon: Icon,
  label,
  value,
  detail,
  percent,
}: {
  icon: LucideIcon;
  label: string;
  value: string;
  detail?: string;
  percent?: number | null;
}) {
  return (
    <div className="rounded-lg border border-border bg-card p-4">
      <p className="flex items-center gap-2 text-muted-foreground">
        <Icon aria-hidden="true" className="size-4" />
        {label}
      </p>
      <p className="mt-1 text-2xl font-semibold tabular-nums">{value}</p>
      {percent !== undefined && percent !== null && (
        <div
          role="meter"
          aria-label={label}
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={Math.round(percent)}
          className="mt-2 h-1 overflow-hidden rounded-full bg-track"
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
        // Nothing is read while the window is hidden in the tray.
        if (!document.hidden) {
          const next = await bridge.monitorLive();
          if (!stopped) {
            setStats(next);
          }
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
      <StatTile icon={Cpu} label="Processor" value={`${Math.round(stats.cpuPercent)}%`} percent={stats.cpuPercent} />
      <StatTile
        icon={MemoryStick}
        label="Memory"
        value={`${Math.round(memory)}%`}
        percent={memory}
        detail={`${formatBytes(stats.memoryUsedBytes)} of ${formatBytes(stats.memoryTotalBytes)}`}
      />
      <StatTile
        icon={HardDrive}
        label="Disk"
        value={stats.diskPercent == null ? "Not available" : `${Math.round(stats.diskPercent)}%`}
        percent={stats.diskPercent}
      />
      <StatTile
        icon={MonitorCog}
        label="Graphics"
        value={gpu == null ? "Not available" : `${Math.round(gpu)}%`}
        percent={gpu}
        detail={stats.gpuTemperature == null ? undefined : `${Math.round(stats.gpuTemperature)} °C`}
      />
      <StatTile icon={Download} label="Download" value={`${formatBytes(stats.networkDownPerSecond)}/s`} />
      <StatTile icon={Upload} label="Upload" value={`${formatBytes(stats.networkUpPerSecond)}/s`} />
    </div>
  );
}

function HealthHero({ report, error, onCheck }: { report: HealthReport | null; error: string | null; onCheck: () => void }) {
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);

  async function save() {
    setSaving(true);
    setSaved(null);
    setSaveError(null);
    try {
      const result = await bridge.reportSave();
      if (result.saved) {
        setSaved(`Saved to ${result.path}`);
      }
    } catch (reason) {
      setSaveError((reason as Error).message);
    } finally {
      setSaving(false);
    }
  }

  const issues = report?.checks.filter((check) => check.status === "warn" || check.status === "bad").length ?? 0;
  const problem = error ?? saveError;
  return (
    <section aria-labelledby="dashboard-title" className="hero flex flex-wrap items-center gap-6 p-6">
      {report && <ScoreRing score={report.score} />}
      <div className="min-w-0 flex-1">
        <h1 id="dashboard-title" className="text-[27px] leading-tight font-semibold tracking-tight">
          {report ? headline(report.score) : "PC health"}
        </h1>
        <p className="mt-1 text-muted-foreground">
          {report
            ? issues === 0
              ? "Nothing needs a look right now."
              : `${issues} ${issues === 1 ? "thing" : "things"} could use a look.`
            : !error && "Checking this PC."}
        </p>
        <div role="status">
          {saved && <p className="mt-2 font-medium">{saved}</p>}
          {problem && <p className="mt-2 font-medium">{problem}</p>}
        </div>
        <div className="mt-3 flex gap-2">
          <button
            type="button"
            onClick={onCheck}
            disabled={!report && !error}
            className="min-h-11 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary disabled:cursor-wait disabled:opacity-60"
          >
            Check again
          </button>
          <button
            type="button"
            onClick={save}
            disabled={saving}
            className="min-h-11 cursor-pointer rounded-md border border-track px-4 font-medium disabled:cursor-wait disabled:opacity-60"
          >
            {saving ? "Preparing" : "Save report"}
          </button>
        </div>
      </div>
    </section>
  );
}

function HealthChecks({ report }: { report: HealthReport }) {
  return (
    <section aria-labelledby="checks-title">
      <h2 id="checks-title" className="mb-2 font-semibold text-muted-foreground">
        Health checks
      </h2>
      <ul className="grid gap-2 lg:grid-cols-2">
        {report.checks.map((item) => {
          const style = statusStyles[item.status];
          const Icon = style.icon;
          return (
            <li key={item.id} className="flex items-center gap-3 rounded-lg border border-border bg-card p-3">
              <Icon aria-hidden="true" className={`size-5 shrink-0 ${style.color}`} />
              <div className="min-w-0">
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
    </section>
  );
}

function QuickActions({ onNavigate }: { onNavigate: (page: string) => void }) {
  return (
    <section aria-labelledby="actions-title">
      <h2 id="actions-title" className="mb-2 font-semibold text-muted-foreground">
        Quick actions
      </h2>
      <div className="grid grid-cols-2 gap-2 lg:grid-cols-3">
        {quickActions.map((action) => {
          const Icon = action.icon;
          return (
            <button
              key={action.page}
              type="button"
              onClick={() => onNavigate(action.page)}
              className="flex cursor-pointer items-center gap-3 rounded-lg border border-border bg-card p-3 text-left hover:bg-muted"
            >
              <Icon aria-hidden="true" className="size-5 shrink-0 text-primary" />
              <span className="min-w-0 flex-1">
                <span className="block font-medium">{action.title}</span>
                <span className="block text-sm text-muted-foreground">{action.note}</span>
              </span>
              <ChevronRight aria-hidden="true" className="size-4 shrink-0 text-muted-foreground" />
            </button>
          );
        })}
      </div>
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
    <section aria-labelledby="hardware-title" className="rounded-lg border border-border bg-card p-5">
      <h2 id="hardware-title" className="font-semibold">
        {system.computer}
      </h2>
      <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-6 gap-y-1">
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

export function DashboardPage({ onNavigate = () => undefined }: { onNavigate?: (page: string) => void }) {
  const [report, setReport] = useState<HealthReport | null>(null);
  const [error, setError] = useState<string | null>(null);

  const check = useCallback(() => {
    setReport(null);
    setError(null);
    bridge.healthCheck().then(setReport, (reason: Error) => setError(reason.message));
  }, []);
  useEffect(check, [check]);

  return (
    <div className="flex flex-col gap-5">
      <HealthHero report={report} error={error} onCheck={check} />
      <LiveTiles />
      <QuickActions onNavigate={onNavigate} />
      {report && <HealthChecks report={report} />}
      <HardwareCard />
    </div>
  );
}
