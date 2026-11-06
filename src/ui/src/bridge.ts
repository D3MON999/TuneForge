export interface PingResult {
  protocolVersion: number;
  engineVersion: string;
  elevated: boolean;
  processId: number;
}

export type TweakState = "applied" | "not-applied" | "unsupported";
export type RestartNeed = "none" | "explorer" | "signout" | "reboot";

export interface Tweak {
  id: string;
  title: string;
  category: string;
  group: string | null;
  risk: "low" | "medium";
  impact: string | null;
  restart: RestartNeed;
  presets: string[];
  requiresAdmin: boolean;
  state: TweakState;
}

export interface TweakPreview {
  id: string;
  title: string;
  restart: RestartNeed;
  impact: string | null;
  changes: { setting: string; before: string; after: string }[];
}

export interface ApplyResult {
  changeSetId: number;
  restorePoint: "requested" | "unavailable" | "skipped";
  results: { id: string; status: string; message: string | null }[];
}

export interface UndoResult {
  changeSetId: number;
  restored: number;
  problems: string[];
}

export interface ChangeSet {
  id: number;
  createdUtc: string;
  restorePoint: string;
  undoneUtc?: string | null;
  tweakIds: string[];
}

export interface OperationResult {
  ok: boolean;
  message: string;
  restartNeeded: boolean;
}

export interface CleanupCategory {
  id: string;
  title: string;
  note: string;
  needsAdmin: boolean;
  defaultOn: boolean;
  bytes: number;
  files: number;
}

export interface CleanupResult {
  freedBytes: number;
  files: number;
  skipped: number;
}

export interface StartupEntry {
  source: string;
  name: string;
  command: string;
  enabled: boolean;
  needsAdmin: boolean;
}

export interface ServiceEntry {
  name: string;
  displayName: string;
  running: boolean;
  startType: string;
  protected: boolean;
}

export interface FeatureEntry {
  id: string;
  title: string;
  note: string;
  state: 'enabled' | 'disabled' | 'unavailable';
}

export interface RepairInfo {
  id: string;
  title: string;
  note: string;
  longRunning: boolean;
}

export interface BuiltInApp {
  id: string;
  title: string;
  note: string;
  group: string;
  safety: "recommended" | "optional" | "caution";
  installed: boolean;
  canReinstall: boolean;
}

export interface BuiltInRemoveResult {
  removed: string[];
  failed: string[];
}

export interface SoftwareEntry {
  id: string;
  title: string;
  note: string;
  category: string;
  installed: boolean;
}

export interface SoftwareList {
  wingetAvailable: boolean;
  items: SoftwareEntry[];
}

export interface InstalledProgram {
  name: string;
  id: string;
  version: string;
  available?: string | null;
  locked: boolean;
}

export interface InstalledList {
  wingetAvailable: boolean;
  items: InstalledProgram[];
}

export interface SystemSummary {
  computer: string;
  windows: string;
  build: string;
  board: string;
  cpu: string;
  cores: number;
  threads: number;
  gpus: string[];
  memoryBytes: number;
  uptimeSeconds: number;
}

export interface LiveStats {
  cpuPercent: number;
  memoryUsedBytes: number;
  memoryTotalBytes: number;
  diskPercent?: number | null;
  networkDownPerSecond: number;
  networkUpPerSecond: number;
  gpuPercent?: number | null;
  gpuTemperature?: number | null;
}

export interface DiskInfo {
  name: string;
  kind: string;
  sizeBytes: number;
  health: "healthy" | "warning" | "unhealthy" | "unknown";
}

export interface VolumeInfo {
  name: string;
  label: string;
  totalBytes: number;
  freeBytes: number;
}

export interface StorageReport {
  disks: DiskInfo[];
  volumes: VolumeInfo[];
}

export interface ErrorGroup {
  source: string;
  eventId: number;
  count: number;
  lastUtc: string;
  message: string;
}

export interface WindowsEvent {
  timeUtc: string;
  log: string;
  source: string;
  eventId: number;
  level: string;
  message: string;
}

export interface HealthCheck {
  id: string;
  title: string;
  status: "good" | "warn" | "bad" | "unknown";
  detail: string;
}

export interface HealthReport {
  score: number;
  checks: HealthCheck[];
}

export interface ReportResult {
  saved: boolean;
  path?: string | null;
}

export interface ProbeSummary {
  host: string;
  address: string;
  sent: number;
  received: number;
  minMs?: number | null;
  avgMs?: number | null;
  maxMs?: number | null;
  jitterMs?: number | null;
  lossPercent: number;
}

export interface TraceHop {
  hop: number;
  address?: string | null;
  sent: number;
  received: number;
  avgMs?: number | null;
  bestMs?: number | null;
  worstMs?: number | null;
  lossPercent: number;
}

export interface TraceResult {
  host: string;
  address: string;
  reached: boolean;
  hops: TraceHop[];
}

export interface DnsPreset {
  id: string;
  title: string;
  note: string;
  primary: string;
  secondary: string;
}

export interface DnsStatus {
  adapters: { name: string; servers: string[] }[];
  presets: DnsPreset[];
  canRestore: boolean;
}

export interface DnsBenchmarkRow {
  id: string;
  title: string;
  server: string;
  medianMs?: number | null;
  failed: number;
}

export interface DnsSetRequest {
  mode: "preset" | "custom" | "automatic" | "previous";
  presetId?: string;
  primary?: string;
  secondary?: string;
}

export interface SettingsChoices {
  closeToTray: boolean;
  startWithWindows: boolean;
  notifications: boolean;
  memoryAuto: boolean;
  memoryThreshold: number;
  cleanAuto: boolean;
  cleanDays: number;
  startupWatch: boolean;
}

export interface AppSettings {
  choices: SettingsChoices;
  lastCleanUtc?: string | null;
}

export interface MemoryResult {
  usedBeforeBytes: number;
  usedAfterBytes: number;
  totalBytes: number;
  processes: number;
  freedBytes: number;
}

interface BridgeReply {
  id: string;
  ok: boolean;
  result?: unknown;
  error?: string;
}

/** The part of WebView2's page API that the app uses. */
export interface WebViewPort {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (event: { data: BridgeReply }) => void): void;
}

declare global {
  interface Window {
    chrome?: { webview?: WebViewPort };
  }
}

export class Bridge {
  private nextId = 0;
  private readonly pending = new Map<
    string,
    { resolve: (value: unknown) => void; reject: (reason: Error) => void }
  >();

  constructor(private readonly port: WebViewPort | undefined) {
    port?.addEventListener("message", (event) => this.receive(event.data));
  }

  get available(): boolean {
    return this.port !== undefined;
  }

  private receive(reply: BridgeReply): void {
    const entry = this.pending.get(reply.id);
    if (!entry) {
      return;
    }

    this.pending.delete(reply.id);
    if (reply.ok) {
      entry.resolve(reply.result);
    } else {
      entry.reject(new Error(reply.error ?? "The request failed."));
    }
  }

  private call<T>(op: string, payload?: unknown): Promise<T> {
    if (!this.port) {
      return Promise.reject(new Error("The TuneForge desktop host is not available."));
    }

    const port = this.port;
    const id = String(++this.nextId);
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve: (value) => resolve(value as T), reject });
      port.postMessage({ id, op, payload });
    });
  }

  connectEngine = () => this.call<PingResult>("engine.connect");
  scan = () => this.call<Tweak[]>("tweaks.scan");
  preview = (ids: string[]) => this.call<TweakPreview[]>("tweaks.preview", { ids });
  apply = (ids: string[], createRestorePoint: boolean) =>
    this.call<ApplyResult>("tweaks.apply", { ids, createRestorePoint });
  history = () => this.call<ChangeSet[]>("history.list");
  undo = (changeSetId: number) => this.call<UndoResult>("history.undo", { changeSetId });
  cleanupScan = () => this.call<CleanupCategory[]>("cleanup.scan");
  cleanupRun = (ids: string[]) => this.call<CleanupResult>("cleanup.run", { ids });
  startupList = () => this.call<StartupEntry[]>("startup.list");
  startupSet = (source: string, name: string, enabled: boolean) =>
    this.call<OperationResult>("startup.set", { source, name, enabled });
  serviceList = () => this.call<ServiceEntry[]>("service.list");
  serviceSet = (name: string, startType: string) =>
    this.call<OperationResult>("service.set", { name, startType });
  featureList = () => this.call<FeatureEntry[]>("feature.list");
  featureSet = (id: string, enabled: boolean) => this.call<OperationResult>("feature.set", { id, enabled });
  repairList = () => this.call<RepairInfo[]>("repair.list");
  repairRun = (id: string) => this.call<OperationResult>("repair.run", { id });
  builtInList = () => this.call<BuiltInApp[]>("builtin.list");
  builtInRemove = (ids: string[]) => this.call<BuiltInRemoveResult>("builtin.remove", { ids });
  builtInReinstall = (id: string) => this.call<OperationResult>("builtin.reinstall", { id });
  softwareList = () => this.call<SoftwareList>("software.list");
  softwareInstall = (id: string) => this.call<OperationResult>("software.install", { id });
  programsList = () => this.call<InstalledList>("programs.list");
  programsUpdate = (id: string) => this.call<OperationResult>("programs.update", { id });
  programsUninstall = (id: string) => this.call<OperationResult>("programs.uninstall", { id });
  systemSummary = () => this.call<SystemSummary>("system.summary");
  monitorLive = () => this.call<LiveStats>("monitor.live");
  healthCheck = () => this.call<HealthReport>("health.check");
  storageRead = () => this.call<StorageReport>("storage.read");
  eventErrors = () => this.call<ErrorGroup[]>("events.errors");
  eventRead = (log: string, level: string) => this.call<WindowsEvent[]>("events.read", { log, level });
  reportSave = () => this.call<ReportResult>("report.save");
  networkPing = (host: string) => this.call<ProbeSummary>("network.ping", { host });
  networkTcpPing = (host: string, port: number) => this.call<ProbeSummary>("network.tcping", { host, port });
  networkTrace = (host: string) => this.call<TraceResult>("network.trace", { host });
  dnsStatus = () => this.call<DnsStatus>("dns.status");
  dnsBenchmark = () => this.call<DnsBenchmarkRow[]>("dns.benchmark");
  dnsSet = (request: DnsSetRequest) => this.call<OperationResult>("dns.set", request);
  dnsFlush = () => this.call<OperationResult>("dns.flush");
  settingsGet = () => this.call<AppSettings>("settings.get");
  settingsSave = (choices: SettingsChoices) => this.call<AppSettings>("settings.save", choices);
  memoryClean = () => this.call<MemoryResult>("memory.clean");
}

export const bridge = new Bridge(window.chrome?.webview);
