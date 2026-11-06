export interface PingResult {
  protocolVersion: number;
  engineVersion: string;
  elevated: boolean;
  processId: number;
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

  private call(op: string): Promise<unknown> {
    if (!this.port) {
      return Promise.reject(new Error("The TuneForge desktop host is not available."));
    }

    const port = this.port;
    const id = String(++this.nextId);
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      port.postMessage({ id, op });
    });
  }

  connectEngine(): Promise<PingResult> {
    return this.call("engine.connect") as Promise<PingResult>;
  }
}

export const bridge = new Bridge(window.chrome?.webview);
