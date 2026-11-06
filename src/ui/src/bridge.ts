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
  Id: number;
  CreatedUtc: string;
  RestorePoint: string;
  UndoneUtc: string | null;
  TweakIds: string[];
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
}

export const bridge = new Bridge(window.chrome?.webview);
