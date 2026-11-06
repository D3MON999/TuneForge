import { useCallback, useEffect, useState, type FormEvent } from "react";
import {
  bridge,
  type ConnectionEntry,
  type DnsBenchmarkRow,
  type DnsSetRequest,
  type DnsStatus,
  type OperationResult,
  type ProbeSummary,
  type TraceResult,
} from "./bridge";
import { BandwidthCard, PingMonitor, SpeedCard } from "./ExtraPanels";

type Tab = "tests" | "dns" | "connections";
type Tool = "ping" | "tcping" | "trace";

const tabs: { id: Tab; label: string }[] = [
  { id: "tests", label: "Connection tests" },
  { id: "dns", label: "DNS" },
  { id: "connections", label: "Connections" },
];

const buttonClass =
  "min-h-11 shrink-0 cursor-pointer rounded-md border border-border px-4 font-medium disabled:cursor-not-allowed disabled:opacity-60";
const primaryClass =
  "min-h-11 shrink-0 cursor-pointer rounded-md bg-primary px-4 font-medium text-on-primary disabled:cursor-not-allowed disabled:opacity-60";
const fieldClass = "min-h-11 rounded-md border border-border bg-card px-3";

const ms = (value?: number | null) => (value == null ? "None" : `${value.toFixed(1)} ms`);

function ConnectionsTab() {
  const [rows, setRows] = useState<ConnectionEntry[] | null>(null);
  const [history, setHistory] = useState<{ title: string; timeUtc: string }[]>([]);
  const [listening, setListening] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const scan = useCallback(() => {
    bridge.connectionsScan().then(
      (found) => {
        setRows(found);
        bridge.connectionsHistory().then(setHistory, () => setHistory([]));
      },
      (reason: Error) => setError(reason.message),
    );
  }, []);
  useEffect(scan, [scan]);

  if (error) {
    return <p role="alert" className="font-medium text-destructive">{error}</p>;
  }

  const shown = (rows ?? []).filter((row) => row.listening === listening);
  return (
    <div>
      <p className="mb-3 text-muted-foreground">Which programs are connected to the internet, and which are waiting for connections. Unknown programs are worth a closer look.</p>
      <div className="mb-3 flex items-center justify-between gap-2">
        <div role="group" aria-label="Connection type" className="flex gap-0.5 rounded-md bg-muted p-[3px]">
          {[false, true].map((value) => (
            <button
              key={String(value)}
              type="button"
              aria-pressed={listening === value}
              onClick={() => setListening(value)}
              className={`min-h-8 cursor-pointer rounded-md px-3.5 font-medium ${listening === value ? "bg-card" : "text-muted-foreground"}`}
            >
              {value ? "Open ports" : "Connected"}
            </button>
          ))}
        </div>
        <button type="button" onClick={scan} className={buttonClass}>
          Scan again
        </button>
      </div>
      {!rows && <p className="text-muted-foreground">Reading connections.</p>}
      <div className="overflow-x-auto rounded-md border border-border bg-card p-3">
        <table className="w-full text-left text-sm tabular-nums">
          <thead className="text-muted-foreground">
            <tr>
              <th scope="col" className="pr-4 font-medium">Program</th>
              <th scope="col" className="pr-4 font-medium">{listening ? "Port" : "Connected to"}</th>
              <th scope="col" className="font-medium">Type</th>
            </tr>
          </thead>
          <tbody>
            {shown.slice(0, 200).map((row, index) => (
              <tr key={index}>
                <td className="pr-4">{row.program}</td>
                <td className="pr-4">{listening ? row.local : row.remote}</td>
                <td>{row.protocol}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {history.length > 0 && (
        <section aria-labelledby="seen-title" className="mt-5">
          <h2 id="seen-title" className="mb-2 font-semibold text-muted-foreground">
            Seen since TuneForge opened
          </h2>
          <ul className="flex flex-col gap-1 text-sm">
            {history.slice(0, 30).map((item) => (
              <li key={item.title} className="flex justify-between gap-3">
                <span className="min-w-0 break-all">{item.title}</span>
                <span className="shrink-0 text-muted-foreground">{new Date(item.timeUtc).toLocaleTimeString()}</span>
              </li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}

export function NetworkPage({ initialTab }: { initialTab?: string }) {
  const [tab, setTab] = useState<Tab>((initialTab as Tab) ?? "tests");

  return (
    <div>
      <div role="tablist" aria-label="Network sections" className="flex flex-wrap gap-2">
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
        {tab === "tests" && (
          <div className="flex flex-col gap-5">
            <ConnectionTests />
            <SpeedCard />
            <PingMonitor />
            <BandwidthCard />
          </div>
        )}
        {tab === "dns" && <DnsTab />}
        {tab === "connections" && <ConnectionsTab />}
      </div>
    </div>
  );
}

function ConnectionTests() {
  const [tool, setTool] = useState<Tool>("ping");
  const [host, setHost] = useState("1.1.1.1");
  const [port, setPort] = useState("443");
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [summary, setSummary] = useState<ProbeSummary | null>(null);
  const [trace, setTrace] = useState<TraceResult | null>(null);

  async function run(event: FormEvent) {
    event.preventDefault();
    setRunning(true);
    setError(null);
    setSummary(null);
    setTrace(null);
    try {
      if (tool === "trace") {
        setTrace(await bridge.networkTrace(host));
      } else if (tool === "tcping") {
        setSummary(await bridge.networkTcpPing(host, Number(port)));
      } else {
        setSummary(await bridge.networkPing(host));
      }
    } catch (reason) {
      setError((reason as Error).message);
    } finally {
      setRunning(false);
    }
  }

  return (
    <div>
      <form onSubmit={run} className="mb-4 flex flex-wrap gap-2">
        <select
          aria-label="Test"
          value={tool}
          onChange={(event) => setTool(event.target.value as Tool)}
          className={`${fieldClass} cursor-pointer`}
        >
          <option value="ping">Ping</option>
          <option value="tcping">TCP ping</option>
          <option value="trace">Route test</option>
        </select>
        <input
          aria-label="Website or IP address"
          required
          value={host}
          onChange={(event) => setHost(event.target.value)}
          className={`${fieldClass} min-w-0 flex-1`}
        />
        {tool === "tcping" && (
          <input
            aria-label="Port"
            type="number"
            required
            min={1}
            max={65535}
            value={port}
            onChange={(event) => setPort(event.target.value)}
            className={`${fieldClass} w-24`}
          />
        )}
        <button type="submit" disabled={running} aria-busy={running} className={primaryClass}>
          {running ? "Testing" : "Start"}
        </button>
      </form>

      <div role="status">
        {running && <p className="text-muted-foreground">{tool === "trace" ? "This can take a minute." : "Sending 10 tests."}</p>}
        {error && <p className="font-medium text-destructive">{error}</p>}
      </div>

      {summary && (
        <div className="rounded-md border border-border bg-card p-4">
          <p className="font-medium">
            {summary.host} ({summary.address})
          </p>
          <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-6 gap-y-1">
            <dt className="text-muted-foreground">Average</dt>
            <dd className="tabular-nums">{ms(summary.avgMs)}</dd>
            <dt className="text-muted-foreground">Best and worst</dt>
            <dd className="tabular-nums">
              {ms(summary.minMs)}, {ms(summary.maxMs)}
            </dd>
            <dt className="text-muted-foreground">Jitter</dt>
            <dd className="tabular-nums">{ms(summary.jitterMs)}</dd>
            <dt className="text-muted-foreground">Packet loss</dt>
            <dd className="tabular-nums">
              {Math.round(summary.lossPercent)}% ({summary.received} of {summary.sent} answered)
            </dd>
          </dl>
        </div>
      )}

      {trace && (
        <div className="rounded-md border border-border bg-card p-4">
          <p className="font-medium">
            {trace.reached ? `Reached ${trace.host} in ${trace.hops.length} hops` : `Didn't reach ${trace.host}`}
          </p>
          <div className="mt-2 overflow-x-auto">
            <table className="w-full text-left text-sm tabular-nums">
              <thead className="text-muted-foreground">
                <tr>
                  <th scope="col" className="pr-4 font-medium">Hop</th>
                  <th scope="col" className="pr-4 font-medium">Address</th>
                  <th scope="col" className="pr-4 font-medium">Average</th>
                  <th scope="col" className="pr-4 font-medium">Worst</th>
                  <th scope="col" className="font-medium">Loss</th>
                </tr>
              </thead>
              <tbody>
                {trace.hops.map((hop) => (
                  <tr key={hop.hop}>
                    <td className="pr-4">{hop.hop}</td>
                    <td className="pr-4">{hop.address ?? "No answer"}</td>
                    <td className="pr-4">{ms(hop.avgMs)}</td>
                    <td className="pr-4">{ms(hop.worstMs)}</td>
                    <td>{Math.round(hop.lossPercent)}%</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </div>
  );
}

function DnsTab() {
  const [status, setStatus] = useState<DnsStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<OperationResult | string | null>(null);
  const [benchmark, setBenchmark] = useState<DnsBenchmarkRow[] | null>(null);
  const [testing, setTesting] = useState(false);
  const [primary, setPrimary] = useState("");
  const [secondary, setSecondary] = useState("");

  const reload = useCallback(() => {
    bridge.dnsStatus().then(setStatus, (reason: Error) => setError(reason.message));
  }, []);
  useEffect(reload, [reload]);

  async function change(request: DnsSetRequest) {
    setBusy(true);
    try {
      setResult(await bridge.dnsSet(request));
      reload();
    } catch (reason) {
      setResult((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function flush() {
    setBusy(true);
    try {
      setResult(await bridge.dnsFlush());
    } catch (reason) {
      setResult((reason as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function runBenchmark() {
    setTesting(true);
    setBenchmark(null);
    try {
      setBenchmark(await bridge.dnsBenchmark());
    } catch (reason) {
      setResult((reason as Error).message);
    } finally {
      setTesting(false);
    }
  }

  if (error) {
    return <p role="alert" className="font-medium text-destructive">{error}</p>;
  }
  if (!status) {
    return <p className="text-muted-foreground">Reading DNS settings.</p>;
  }

  const text = typeof result === "string" ? result : result?.message;
  const failed = typeof result === "string" || result?.ok === false;
  return (
    <div className="flex flex-col gap-6">
      <div role="status">{text && <p className={`font-medium ${failed ? "text-destructive" : "text-success"}`}>{text}</p>}</div>

      <section aria-labelledby="dns-current" className="rounded-lg border border-border bg-card p-6">
        <h2 id="dns-current" className="text-lg font-semibold">
          Current DNS
        </h2>
        {status.adapters.length === 0 && <p className="mt-1 text-muted-foreground">No connected network adapter was found.</p>}
        {status.adapters.map((adapter) => (
          <p key={adapter.name} className="mt-1 break-words">
            <span className="font-medium">{adapter.name}:</span> {adapter.servers.join(", ") || "None"}
          </p>
        ))}
        <div className="mt-3 flex flex-wrap gap-2">
          <button type="button" disabled={busy} onClick={() => change({ mode: "automatic" })} className={buttonClass}>
            Use automatic
          </button>
          {status.canRestore && (
            <button type="button" disabled={busy} onClick={() => change({ mode: "previous" })} className={buttonClass}>
              Restore earlier DNS
            </button>
          )}
          <button type="button" disabled={busy} onClick={flush} className={buttonClass}>
            Clear DNS cache
          </button>
        </div>
      </section>

      <section aria-labelledby="dns-speed" className="rounded-lg border border-border bg-card p-6">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h2 id="dns-speed" className="text-lg font-semibold">
              Speed test
            </h2>
            <p className="text-muted-foreground">Times 5 lookups against each provider, from this connection.</p>
          </div>
          <button type="button" disabled={testing} aria-busy={testing} onClick={runBenchmark} className={primaryClass}>
            {testing ? "Testing" : "Run speed test"}
          </button>
        </div>
        {benchmark && (
          <table className="mt-3 w-full text-left tabular-nums">
            <thead className="text-sm text-muted-foreground">
              <tr>
                <th scope="col" className="font-medium">Provider</th>
                <th scope="col" className="font-medium">Typical time</th>
                <th scope="col"><span className="sr-only">Action</span></th>
              </tr>
            </thead>
            <tbody>
              {benchmark.map((row) => (
                <tr key={row.id}>
                  <td className="py-1 pr-4">{row.title}</td>
                  <td className="py-1 pr-4">
                    {row.medianMs == null ? "No answer" : ms(row.medianMs)}
                    {row.failed > 0 && row.medianMs != null ? `, ${row.failed} failed` : ""}
                  </td>
                  <td className="py-1 text-right">
                    {row.id !== "current" && (
                      <button
                        type="button"
                        disabled={busy}
                        aria-label={`Use ${row.title}`}
                        onClick={() => change({ mode: "preset", presetId: row.id })}
                        className={buttonClass}
                      >
                        Use
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      <section aria-labelledby="dns-providers" className="rounded-lg border border-border bg-card p-6">
        <h2 id="dns-providers" className="text-lg font-semibold">
          Providers
        </h2>
        <div className="mt-3 flex flex-col gap-2">
          {status.presets.map((preset) => (
            <div key={preset.id} className="flex items-center justify-between gap-3">
              <div className="min-w-0">
                <p className="font-medium">{preset.title}</p>
                <p className="text-sm text-muted-foreground">
                  {preset.note} {preset.primary}, {preset.secondary}
                </p>
              </div>
              <button
                type="button"
                disabled={busy}
                aria-label={`Switch to ${preset.title}`}
                onClick={() => change({ mode: "preset", presetId: preset.id })}
                className={buttonClass}
              >
                Use
              </button>
            </div>
          ))}
        </div>
      </section>

      <section aria-labelledby="dns-custom" className="rounded-lg border border-border bg-card p-6">
        <h2 id="dns-custom" className="text-lg font-semibold">
          Custom DNS
        </h2>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            void change({ mode: "custom", primary, secondary: secondary || undefined });
          }}
          className="mt-3 flex flex-wrap gap-2"
        >
          <input
            aria-label="Primary DNS server"
            required
            placeholder="Primary"
            value={primary}
            onChange={(event) => setPrimary(event.target.value)}
            className={`${fieldClass} min-w-0 flex-1`}
          />
          <input
            aria-label="Secondary DNS server"
            placeholder="Secondary (optional)"
            value={secondary}
            onChange={(event) => setSecondary(event.target.value)}
            className={`${fieldClass} min-w-0 flex-1`}
          />
          <button type="submit" disabled={busy} className={primaryClass}>
            Apply
          </button>
        </form>
      </section>
    </div>
  );
}
