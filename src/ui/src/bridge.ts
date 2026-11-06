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
  risk: "low" | "medium" | "high";
  impact: string | null;
  restart: RestartNeed;
  presets: string[];
  requiresAdmin: boolean;
  state: TweakState;
  description: string;
  hazard?: boolean;
}

export interface TweakPreview {
  id: string;
  title: string;
  restart: RestartNeed;
  impact: string | null;
  changes: { setting: string; before: string; after: string }[];
  hazard?: boolean;
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
  ignored: boolean;
  worsened: boolean;
}

export interface HealthReport {
  score: number;
  fullScore: number;
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
  registryBackup: boolean;
  crashLogs: boolean;
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
  skipped?: boolean;
}

export interface GameBoosts {
  highPriority: boolean;
  highPerformanceGpu: boolean;
  disableFullscreenOptimizations: boolean;
  powerBoost: boolean;
  focus: boolean;
}

export interface GameEntry {
  id: string;
  title: string;
  launcher: string;
  folder: string;
  executable: string;
  antiCheat?: string | null;
  boosts: GameBoosts;
}

export interface PowerPlan {
  id: string;
  name: string;
  active: boolean;
}

export interface GamingOverview {
  resizeBar: "on" | "off" | "unknown";
  plans: PowerPlan[];
  focusApps: string[];
  runningApps: string[];
}

export interface UpdateStatus {
  current: string;
  latest?: string | null;
  available: boolean;
  message: string;
}

export interface AdvancedEntry {
  id: string;
  title: string;
  description: string;
  warning: string;
  risk: "low" | "medium" | "high";
  restartNeeded: boolean;
  reversible: boolean;
  enabled: boolean;
  hazard?: boolean;
}

export interface SecurityStatus {
  antivirus?: boolean | null;
  realTimeProtection?: boolean | null;
  controlledFolderAccess?: boolean | null;
  firewall?: boolean | null;
  bitLocker?: boolean | null;
}

export interface ToolLink {
  id: string;
  title: string;
  note: string;
}

export interface ProcessEntry {
  id: number;
  name: string;
  memoryBytes: number;
  hasWindow: boolean;
  locked: boolean;
}

export interface DisplayMode {
  name: string;
  currentHz: number;
  bestHz: number;
}

export interface ConnectionEntry {
  protocol: string;
  local: string;
  remote: string;
  state: string;
  processId: number;
  program: string;
  listening: boolean;
}

export interface ImportResult {
  tweaks: string[];
  apps: string[];
  ignored: number;
}

export interface SpeedResult {
  downloadMbps?: number | null;
  uploadMbps?: number | null;
  error?: string | null;
}

export interface BandwidthRule {
  program: string;
  mbps: number;
}

export interface GpuHealth {
  name: string;
  driver: string;
  temperatureC?: number | null;
  fanPercent?: number | null;
  powerWatts?: number | null;
  performance: string;
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
  apply = (ids: string[], createRestorePoint: boolean, acceptHazards = false) =>
    this.call<ApplyResult>("tweaks.apply", { ids, createRestorePoint, acceptHazards });
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
  healthIgnore = (id: string, ignored: boolean) => this.call<HealthReport>("health.ignore", { id, ignored });
  windowTheme = (color: string, dark: boolean) => this.call<boolean>("window.theme", { color, dark });
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
  gamesList = () => this.call<GameEntry[]>("games.list");
  gamesSave = (gameId: string, boosts: GameBoosts) => this.call<OperationResult>("games.save", { gameId, boosts });
  gamingOverview = () => this.call<GamingOverview>("gaming.overview");
  powerSet = (id: string) => this.call<OperationResult>("power.set", { id });
  powerUltimate = () => this.call<OperationResult>("power.ultimate");
  focusSave = (names: string[]) => this.call<OperationResult>("focus.save", { names });
  advancedList = () => this.call<AdvancedEntry[]>("advanced.list");
  advancedSet = (id: string, enabled: boolean, acceptHazard = false) =>
    this.call<OperationResult>("advanced.set", { id, enabled, acceptHazard });
  securityRead = () => this.call<SecurityStatus>("security.read");
  securitySet = (id: string) => this.call<OperationResult>("security.set", { id });
  toolsList = () => this.call<ToolLink[]>("tools.list");
  toolsOpen = (id: string) => this.call<OperationResult>("tools.open", { id });
  processList = () => this.call<ProcessEntry[]>("process.list");
  processEnd = (id: number, name: string) => this.call<OperationResult>("process.end", { id, name });
  wingetRepair = () => this.call<OperationResult>("winget.repair");
  powerCreate = (host: string) => this.call<OperationResult>("power.create", { host });
  powerParking = () => this.call<{ minimumCoresPercent?: number | null }>("power.parking");
  powerKeepCores = () => this.call<OperationResult>("power.keepCores");
  displayRead = () => this.call<DisplayMode | null>("display.read");
  displayBest = () => this.call<OperationResult>("display.best");
  connectionsScan = () => this.call<ConnectionEntry[]>("connections.scan");
  connectionsHistory = () => this.call<{ title: string; timeUtc: string }[]>("connections.history");
  settingsExport = () => this.call<ReportResult>("settings.export");
  settingsImport = () => this.call<ImportResult | null>("settings.import");
  speedRun = () => this.call<SpeedResult>("speed.run");
  bandwidthList = () => this.call<BandwidthRule[]>("bandwidth.list");
  bandwidthSet = (program: string, mbps: number) => this.call<OperationResult>("bandwidth.set", { program, mbps });
  bandwidthRemove = (program: string) => this.call<OperationResult>("bandwidth.remove", { program, mbps: 0 });
  gpuHealth = () => this.call<GpuHealth | null>("gpu.health");
  biosRestart = () => this.call<OperationResult>("bios.restart");
  biosCancel = () => this.call<OperationResult>("bios.cancel");
  registryBackup = () => this.call<OperationResult>("registry.backup");
  turboGet = () => this.call<boolean>("turbo.get");
  turboSet = (enabled: boolean) => this.call<boolean>("turbo.set", { enabled });
  gamesVerify = (gameId: string) => this.call<OperationResult>("games.verify", { gameId });
  crashOpen = () => this.call<OperationResult>("crash.open");
  updateCheck = () => this.call<UpdateStatus>("update.check");
  updateInstall = () => this.call<OperationResult>("update.install");
}

export const bridge = new Bridge(window.chrome?.webview);
