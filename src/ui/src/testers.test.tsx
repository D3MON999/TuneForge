// @vitest-environment happy-dom
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { bridge } from "./bridge";
import { detectLayout, EDGE_SLOTS, edgeResult, padName, recordEdge } from "./ControllerTest";
import { TestersPage } from "./TestersPage";

vi.mock("./bridge", () => ({
  bridge: { batteryRead: vi.fn() },
}));

const api = vi.mocked(bridge);

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("Testers page", () => {
  it("tracks tested keys and the most held at once", async () => {
    const user = userEvent.setup();
    render(<TestersPage />);

    const area = screen.getByRole("application", { name: "Keyboard test area" });
    fireEvent.keyDown(area, { code: "KeyA" });
    fireEvent.keyDown(area, { code: "KeyS" });
    fireEvent.keyDown(area, { code: "KeyD" });
    fireEvent.keyUp(area, { code: "KeyA" });

    expect(screen.getByTestId("key-KeyA").getAttribute("data-tested")).toBe("true");
    expect(screen.getByText(/3 of \d+ keys work/)).toBeTruthy();
    expect(screen.getByTestId("most-keys").textContent).toBe("3");

    await user.click(screen.getByRole("button", { name: "Reset" }));
    expect(screen.getByText(/0 of \d+ keys work/)).toBeTruthy();
  });

  it("counts mouse buttons and flags a click that bounced", async () => {
    const user = userEvent.setup();
    render(<TestersPage />);
    await user.click(screen.getByRole("tab", { name: "Mouse" }));

    const pad = screen.getByTestId("mouse-pad");
    fireEvent.mouseDown(pad, { button: 2 });
    expect(screen.getByText("1 clicks")).toBeTruthy();

    // Released and pressed again straight away: faster than a finger, so it counts as a bounce.
    fireEvent.mouseDown(pad, { button: 0 });
    fireEvent.mouseUp(pad, { button: 0 });
    fireEvent.mouseDown(pad, { button: 0 });

    expect(screen.getByText(/accidental double clicks/)).toBeTruthy();
  });

  it("opens straight to a tab from search", () => {
    api.batteryRead.mockResolvedValue({ batteries: [] });
    render(<TestersPage initialTab="battery" />);
    expect(screen.getByRole("tab", { name: "Battery", selected: true })).toBeTruthy();
  });

  it("shows battery health as a share of the original capacity", async () => {
    api.batteryRead.mockResolvedValue({ batteries: [{ name: "Main", maker: "Maker", designMwh: 50000, fullMwh: 41000, cycles: 312 }] });
    render(<TestersPage initialTab="battery" />);

    expect(await screen.findByText("82%")).toBeTruthy();
    expect(screen.getByText("312")).toBeTruthy();
  });

  it("says when there's no battery", async () => {
    api.batteryRead.mockResolvedValue({ batteries: [] });
    render(<TestersPage initialTab="battery" />);
    expect(await screen.findByText(/no battery/)).toBeTruthy();
  });

  it("draws a connected PlayStation controller with its own button names", async () => {
    const pressed = (on: boolean) => ({ pressed: on, touched: on, value: on ? 1 : 0 });
    const pad = {
      id: "DualSense Wireless Controller (STANDARD GAMEPAD Vendor: 054c Product: 0ce6)",
      index: 0,
      mapping: "standard",
      connected: true,
      axes: [0.02, 0, 0, 0],
      buttons: Array.from({ length: 18 }, (_, index) => pressed(index === 0)),
    };
    Object.defineProperty(navigator, "getGamepads", { configurable: true, value: () => [pad, null, null, null] });

    render(<TestersPage initialTab="controller" />);
    expect(await screen.findByText(/PlayStation layout detected/)).toBeTruthy();
    expect(screen.getByTestId("pad-button-0").textContent).toBe("Cross");
    expect(screen.getByTestId("pad-button-0").getAttribute("data-pressed")).toBe("true");
    expect(screen.getByRole("button", { name: /1 · DualSense Wireless Controller/ })).toBeTruthy();
  });
});

describe("controller helpers", () => {
  it("tells the layouts apart", () => {
    expect(detectLayout("Xbox 360 Controller (XInput STANDARD GAMEPAD)")).toBe("xbox");
    expect(detectLayout("Xbox Wireless Controller (STANDARD GAMEPAD Vendor: 045e Product: 0b13)")).toBe("xbox");
    expect(detectLayout("Wireless Controller (STANDARD GAMEPAD Vendor: 054c Product: 09cc)")).toBe("playstation");
    expect(detectLayout("Pro Controller (STANDARD GAMEPAD Vendor: 057e Product: 2009)")).toBe("nintendo");
    expect(detectLayout("USB Gamepad (Vendor: 0079 Product: 0006)")).toBe("standard");
    expect(padName("DualSense Wireless Controller (STANDARD GAMEPAD)")).toBe("DualSense Wireless Controller");
  });

  it("scores a round stick as nearly perfect and a square one as off", () => {
    let round = new Array<number>(EDGE_SLOTS).fill(0);
    let square = new Array<number>(EDGE_SLOTS).fill(0);
    for (let step = 0; step < 720; step++) {
      const turn = (step / 720) * 2 * Math.PI;
      round = recordEdge(round, Math.cos(turn), Math.sin(turn));
      const scale = 1 / Math.max(Math.abs(Math.cos(turn)), Math.abs(Math.sin(turn)));
      square = recordEdge(square, Math.cos(turn) * scale, Math.sin(turn) * scale);
    }

    expect(edgeResult(round).covered).toBe(1);
    expect(edgeResult(round).error).toBeLessThan(0.5);
    expect(edgeResult(square).error).toBeGreaterThan(10);
  });

  it("ignores small pushes", () => {
    const edge = new Array<number>(EDGE_SLOTS).fill(0);
    expect(recordEdge(edge, 0.1, 0.1)).toBe(edge);
  });
});
