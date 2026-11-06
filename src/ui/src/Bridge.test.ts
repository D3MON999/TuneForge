import { beforeAll, describe, expect, it } from "vitest";
import type { Bridge as BridgeClass, WebViewPort } from "./bridge";

let Bridge: typeof BridgeClass;

beforeAll(async () => {
  Object.assign(globalThis, { window: {} });
  ({ Bridge } = await import("./bridge"));
});

function fakePort() {
  const sent: { id: string; op: string }[] = [];
  let listener: Parameters<WebViewPort["addEventListener"]>[1] = () => {};
  const port: WebViewPort = {
    postMessage: (message) => sent.push(message as { id: string; op: string }),
    addEventListener: (_type, handler) => {
      listener = handler;
    },
  };
  return { port, sent, reply: (data: Parameters<typeof listener>[0]["data"]) => listener({ data }) };
}

describe("Bridge", () => {
  it("resolves a call with the host's result", async () => {
    const { port, sent, reply } = fakePort();
    const call = new Bridge(port).connectEngine();
    const result = { protocolVersion: 1, engineVersion: "0.1.0", elevated: true, processId: 5 };

    expect(sent[0].op).toBe("engine.connect");
    reply({ id: sent[0].id, ok: true, result });

    await expect(call).resolves.toEqual(result);
  });

  it("rejects with the host's error text", async () => {
    const { port, sent, reply } = fakePort();
    const call = new Bridge(port).connectEngine();

    reply({ id: sent[0].id, ok: false, error: "Admin permission was declined." });

    await expect(call).rejects.toThrow("Admin permission was declined.");
  });

  it("ignores replies it did not ask for", async () => {
    const { port, sent, reply } = fakePort();
    const call = new Bridge(port).connectEngine();

    reply({ id: "unknown", ok: true });
    reply({ id: sent[0].id, ok: true, result: "done" });

    await expect(call).resolves.toBe("done");
  });

  it("fails clearly outside the desktop host", async () => {
    const bridge = new Bridge(undefined);

    expect(bridge.available).toBe(false);
    await expect(bridge.connectEngine()).rejects.toThrow("not available");
  });
});
