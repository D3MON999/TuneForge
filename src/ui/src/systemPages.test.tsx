// @vitest-environment happy-dom
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AppsPage } from "./AppsPage";
import { bridge } from "./bridge";
import { CleanupPage } from "./CleanupPage";
import { formatBytes } from "./format";
import { RepairPage } from "./RepairPage";

vi.mock("./bridge", () => ({
  bridge: {
    cleanupScan: vi.fn(),
    cleanupRun: vi.fn(),
    startupList: vi.fn(),
    startupSet: vi.fn(),
    serviceList: vi.fn(),
    serviceSet: vi.fn(),
    featureList: vi.fn(),
    featureSet: vi.fn(),
    repairList: vi.fn(),
    repairRun: vi.fn(),
  },
}));

const api = vi.mocked(bridge);

beforeEach(() => {
  vi.resetAllMocks();
  api.startupList.mockResolvedValue([]);
});

afterEach(cleanup);

describe("formatBytes", () => {
  it("uses sensible units", () => {
    expect(formatBytes(512)).toBe("512 B");
    expect(formatBytes(1536)).toBe("1.5 KB");
    expect(formatBytes(50 * 1024 * 1024)).toBe("50 MB");
  });
});

describe("CleanupPage", () => {
  const categories = [
    { id: "temp", title: "Temp", note: "n", needsAdmin: false, defaultOn: true, bytes: 2048, files: 2 },
    { id: "bin", title: "Recycle Bin", note: "n", needsAdmin: false, defaultOn: false, bytes: 4096, files: 1 },
  ];

  it("starts with safe defaults and never deletes without a second confirmation", async () => {
    const user = userEvent.setup();
    api.cleanupScan.mockResolvedValue(categories);
    api.cleanupRun.mockResolvedValue({ freedBytes: 2048, files: 2, skipped: 1 });
    render(<CleanupPage />);

    expect(((await screen.findByLabelText(/Temp/)) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByLabelText(/Recycle Bin/) as HTMLInputElement).checked).toBe(false);

    await user.click(screen.getByRole("button", { name: "Clean 2.0 KB" }));
    expect(api.cleanupRun).not.toHaveBeenCalled();
    expect(screen.getByText(/can't be undone/)).toBeTruthy();

    await user.click(screen.getByRole("button", { name: "Delete files" }));

    await screen.findByText(/Freed 2.0 KB from 2 files/);
    expect(api.cleanupRun).toHaveBeenCalledWith(["temp"]);
    expect(screen.getByText(/1 files were in use/)).toBeTruthy();
  });

  it("can cancel the confirmation", async () => {
    const user = userEvent.setup();
    api.cleanupScan.mockResolvedValue(categories);
    render(<CleanupPage />);

    await user.click(await screen.findByRole("button", { name: "Clean 2.0 KB" }));
    await user.click(screen.getByRole("button", { name: "Cancel" }));

    expect(api.cleanupRun).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Clean 2.0 KB" })).toBeTruthy();
  });

  it("disables cleaning with nothing selected", async () => {
    const user = userEvent.setup();
    api.cleanupScan.mockResolvedValue(categories);
    render(<CleanupPage />);

    await user.click(await screen.findByLabelText(/Temp/));

    expect((screen.getByRole("button", { name: "Clean 0 B" }) as HTMLButtonElement).disabled).toBe(true);
  });
});

describe("RepairPage", () => {
  it("runs a repair and shows the outcome", async () => {
    const user = userEvent.setup();
    api.repairList.mockResolvedValue([{ id: "time", title: "Fix the clock", note: "n", longRunning: false }]);
    api.repairRun.mockResolvedValue({ ok: true, message: "The clock is synced.", restartNeeded: false });
    render(<RepairPage />);

    await user.click(await screen.findByRole("button", { name: "Run Fix the clock" }));

    expect(await screen.findByText("The clock is synced.")).toBeTruthy();
    expect(api.repairRun).toHaveBeenCalledWith("time");
  });

  it("warns about long repairs and shows a declined prompt", async () => {
    const user = userEvent.setup();
    api.repairList.mockResolvedValue([{ id: "sfc", title: "Check", note: "n", longRunning: true }]);
    api.repairRun.mockRejectedValue(new Error("Admin permission was declined."));
    render(<RepairPage />);

    expect(await screen.findByText(/15 minutes/)).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Run Check" }));

    expect(await screen.findByText("Admin permission was declined.")).toBeTruthy();
  });
});

describe("AppsPage", () => {
  it("turns a startup program off and reloads", async () => {
    const user = userEvent.setup();
    api.startupList.mockResolvedValue([
      { source: "hkcu-run", name: "Discord", command: "discord.exe", enabled: true, needsAdmin: false },
    ]);
    api.startupSet.mockResolvedValue({ ok: true, message: "Saved. You can undo this from History.", restartNeeded: false });
    render(<AppsPage />);

    await user.click(await screen.findByLabelText(/Discord/));

    expect(api.startupSet).toHaveBeenCalledWith("hkcu-run", "Discord", false);
    expect(await screen.findByText(/You can undo this/)).toBeTruthy();
    await waitFor(() => expect(api.startupList).toHaveBeenCalledTimes(2));
  });

  it("locks protected services and searches the rest", async () => {
    const user = userEvent.setup();
    api.serviceList.mockResolvedValue([
      { name: "Fax", displayName: "Fax", running: false, startType: "manual", protected: false },
      { name: "WinDefend", displayName: "Microsoft Defender", running: true, startType: "auto", protected: true },
    ]);
    render(<AppsPage />);

    await user.click(screen.getByRole("tab", { name: "Services" }));

    expect(await screen.findByText("Locked")).toBeTruthy();
    expect(screen.queryByLabelText("Startup type for Microsoft Defender")).toBeNull();

    await user.type(screen.getByLabelText("Search services"), "fax");
    expect(screen.queryByText("Microsoft Defender")).toBeNull();
  });

  it("changes a service startup type", async () => {
    const user = userEvent.setup();
    api.serviceList.mockResolvedValue([
      { name: "Fax", displayName: "Fax", running: false, startType: "manual", protected: false },
    ]);
    api.serviceSet.mockResolvedValue({ ok: true, message: "Saved.", restartNeeded: true });
    render(<AppsPage />);

    await user.click(screen.getByRole("tab", { name: "Services" }));
    await user.selectOptions(await screen.findByLabelText("Startup type for Fax"), "disabled");

    expect(api.serviceSet).toHaveBeenCalledWith("Fax", "disabled");
  });

  it("switches features and hides unavailable ones", async () => {
    const user = userEvent.setup();
    api.featureList.mockResolvedValue([
      { id: "sandbox", title: "Windows Sandbox", note: "n", state: "disabled" },
      { id: "hyper-v", title: "Hyper-V", note: "n", state: "unavailable" },
    ]);
    api.featureSet.mockResolvedValue({ ok: true, message: "Restart to finish.", restartNeeded: true });
    render(<AppsPage />);

    await user.click(screen.getByRole("tab", { name: "Windows features" }));
    await user.click(await screen.findByRole("button", { name: "Turn on Windows Sandbox" }));

    expect(api.featureSet).toHaveBeenCalledWith("sandbox", true);
    expect(screen.getByText("Not available")).toBeTruthy();
    expect(await screen.findByText("Restart to finish.")).toBeTruthy();
  });
});
