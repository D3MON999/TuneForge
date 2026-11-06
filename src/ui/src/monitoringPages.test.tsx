// @vitest-environment happy-dom
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { bridge } from "./bridge";
import { DashboardPage } from "./DashboardPage";
import { MonitorPage } from "./MonitorPage";
import { NetworkPage } from "./NetworkPage";

vi.mock("./bridge", () => ({
  bridge: {
    systemSummary: vi.fn(),
    monitorLive: vi.fn(),
    healthCheck: vi.fn(),
    healthIgnore: vi.fn(),
    storageRead: vi.fn(),
    eventErrors: vi.fn(),
    eventRead: vi.fn(),
    reportSave: vi.fn(),
    networkPing: vi.fn(),
    networkTcpPing: vi.fn(),
    networkTrace: vi.fn(),
    dnsStatus: vi.fn(),
    dnsBenchmark: vi.fn(),
    dnsSet: vi.fn(),
    dnsFlush: vi.fn(),
    gpuHealth: vi.fn(),
    bandwidthList: vi.fn(),
    speedRun: vi.fn(),
    connectionsScan: vi.fn(),
    connectionsHistory: vi.fn(),
  },
}));

const api = vi.mocked(bridge);

const live = {
  cpuPercent: 12.4,
  memoryUsedBytes: 8 * 1024 ** 3,
  memoryTotalBytes: 16 * 1024 ** 3,
  diskPercent: 3,
  networkDownPerSecond: 2048,
  networkUpPerSecond: 0,
  gpuPercent: 8,
  gpuTemperature: 55,
};

const system = {
  computer: "DESK",
  windows: "Windows 11 Pro",
  build: "26300",
  board: "Gigabyte X570S",
  cpu: "Ryzen 7",
  cores: 8,
  threads: 16,
  gpus: ["RTX 3060"],
  memoryBytes: 16 * 1024 ** 3,
  uptimeSeconds: 90000,
};

const health = {
  score: 85,
  fullScore: 85,
  checks: [
    { id: "disks", title: "Disk health", status: "good" as const, detail: "2 disks report no problems.", ignored: false, worsened: false },
    { id: "space", title: "Free space", status: "warn" as const, detail: "Low.", ignored: false, worsened: false },
  ],
};

const ignoredHealth = {
  score: 100,
  fullScore: 85,
  checks: [health.checks[0], { ...health.checks[1], ignored: true }],
};

beforeEach(() => {
  vi.resetAllMocks();
  api.monitorLive.mockResolvedValue(live);
  api.systemSummary.mockResolvedValue(system);
  api.healthCheck.mockResolvedValue(health);
  api.gpuHealth.mockResolvedValue(null);
  api.bandwidthList.mockResolvedValue([]);
});

afterEach(cleanup);

describe("DashboardPage", () => {
  it("shows the score, reasons, live usage and hardware", async () => {
    render(<DashboardPage />);

    expect(await screen.findByRole("img", { name: "Health score 85 out of 100" })).toBeTruthy();
    expect(screen.getByText("Your PC is in great shape")).toBeTruthy();
    expect(screen.getByText("1 thing could use a look.")).toBeTruthy();
    expect(screen.getByText("Free space")).toBeTruthy();
    expect(screen.getByText(": Check this")).toBeTruthy();
    expect(await screen.findByText("12%")).toBeTruthy();
    expect(screen.getByText("55 °C")).toBeTruthy();
    expect(await screen.findByText(/Ryzen 7, 8 cores, 16 threads/)).toBeTruthy();
    expect(screen.getByRole("meter", { name: "Processor" }).getAttribute("aria-valuenow")).toBe("12");
  });

  it("says when a value isn't available", async () => {
    api.monitorLive.mockResolvedValue({ ...live, gpuPercent: null, gpuTemperature: null });
    render(<DashboardPage />);

    expect(await screen.findByText("Not available")).toBeTruthy();
    expect(screen.queryByRole("meter", { name: "Graphics" })).toBeNull();
  });

  it("saves a report and stays quiet if the save is cancelled", async () => {
    const user = userEvent.setup();
    api.reportSave.mockResolvedValueOnce({ saved: false });
    api.reportSave.mockResolvedValueOnce({ saved: true, path: "C:\\report.txt" });
    render(<DashboardPage />);
    await screen.findByRole("img", { name: /Health score/ });

    await user.click(screen.getByRole("button", { name: "Save report" }));
    expect(screen.queryByText(/Saved to/)).toBeNull();

    await user.click(screen.getByRole("button", { name: "Save report" }));
    expect(await screen.findByText("Saved to C:\\report.txt")).toBeTruthy();
  });

  it("only offers Ignore on problems", async () => {
    render(<DashboardPage />);

    expect(await screen.findByRole("button", { name: "Ignore Free space" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Ignore Disk health" })).toBeNull();
  });

  it("moves an ignored check to its own group and says what the score would be", async () => {
    const user = userEvent.setup();
    api.healthIgnore.mockResolvedValue(ignoredHealth);
    render(<DashboardPage />);

    await user.click(await screen.findByRole("button", { name: "Ignore Free space" }));

    expect(api.healthIgnore).toHaveBeenCalledWith("space", true);
    expect(await screen.findByRole("img", { name: "Health score 100 out of 100" })).toBeTruthy();
    expect(screen.getByText("1 ignored · 85 if counted")).toBeTruthy();
    expect(screen.getByText("Ignored (1)")).toBeTruthy();
    expect(screen.getByText("Nothing needs a look right now.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Stop ignoring Free space" })).toBeTruthy();
    expect(api.healthCheck).toHaveBeenCalledTimes(1);
  });

  it("can stop ignoring a check", async () => {
    const user = userEvent.setup();
    api.healthCheck.mockResolvedValue(ignoredHealth);
    api.healthIgnore.mockResolvedValue(health);
    render(<DashboardPage />);

    await user.click(await screen.findByRole("button", { name: "Stop ignoring Free space" }));

    expect(api.healthIgnore).toHaveBeenCalledWith("space", false);
    expect(await screen.findByRole("img", { name: "Health score 85 out of 100" })).toBeTruthy();
    expect(screen.queryByText("Ignored (1)")).toBeNull();
    expect(screen.queryByText(/if counted/)).toBeNull();
  });

  it("brings back a check that got worse and can ignore it again", async () => {
    const user = userEvent.setup();
    api.healthCheck.mockResolvedValue({
      score: 70,
      fullScore: 70,
      checks: [{ id: "disks", title: "Disk health", status: "bad", detail: "SSD is failing.", ignored: false, worsened: true }],
    });
    api.healthIgnore.mockResolvedValue(ignoredHealth);
    render(<DashboardPage />);

    expect(await screen.findByText("Worse since you ignored it, so it counts again.")).toBeTruthy();
    expect(screen.getByText("1 thing could use a look.")).toBeTruthy();
    expect(screen.queryByText(/Ignored \(/)).toBeNull();

    await user.click(screen.getByRole("button", { name: "Ignore Disk health" }));
    expect(api.healthIgnore).toHaveBeenCalledWith("disks", true);
  });

  it("keeps a failing disk's warning readable even when it's ignored", async () => {
    api.healthCheck.mockResolvedValue({
      score: 100,
      fullScore: 70,
      checks: [{ id: "disks", title: "Disk health", status: "bad", detail: "SSD is failing. Back up your files now.", ignored: true, worsened: false }],
    });
    render(<DashboardPage />);

    expect((await screen.findByText("SSD is failing. Back up your files now.")).className).toContain("text-destructive");
    expect(screen.getByRole("img", { name: "Health score 100 out of 100" })).toBeTruthy();
  });

  it("can check again", async () => {
    const user = userEvent.setup();
    render(<DashboardPage />);
    await screen.findByRole("img", { name: /Health score/ });

    await user.click(screen.getByRole("button", { name: "Check again" }));

    await waitFor(() => expect(api.healthCheck).toHaveBeenCalledTimes(2));
  });

  it("opens a page from a quick action", async () => {
    const user = userEvent.setup();
    const onNavigate = vi.fn();
    render(<DashboardPage onNavigate={onNavigate} />);

    await user.click(screen.getByRole("button", { name: /Boost a game/ }));

    expect(onNavigate).toHaveBeenCalledWith("gaming");
  });
});

describe("MonitorPage", () => {
  beforeEach(() => {
    api.storageRead.mockResolvedValue({
      disks: [{ name: "CT500", kind: "NVMe SSD", sizeBytes: 500e9, health: "healthy" }],
      volumes: [{ name: "C:\\", label: "Windows", totalBytes: 1000, freeBytes: 250 }],
    });
  });

  it("shows disk health and space", async () => {
    render(<MonitorPage />);

    expect(await screen.findByText("Healthy")).toBeTruthy();
    expect(screen.getByRole("meter", { name: "C:\\ space used" }).getAttribute("aria-valuenow")).toBe("75");
  });

  it("groups Windows errors", async () => {
    const user = userEvent.setup();
    api.eventErrors.mockResolvedValue([
      { source: "Disk", eventId: 7, count: 4, lastUtc: "2026-11-01T00:00:00Z", message: "Bad block." },
    ]);
    render(<MonitorPage />);

    await user.click(screen.getByRole("tab", { name: "Windows errors" }));

    expect(await screen.findByText("4 times")).toBeTruthy();
    expect(screen.getByText("Bad block.")).toBeTruthy();
  });

  it("changes log and level", async () => {
    const user = userEvent.setup();
    api.eventRead.mockResolvedValue([]);
    render(<MonitorPage />);

    await user.click(screen.getByRole("tab", { name: "Logs" }));
    await user.selectOptions(await screen.findByLabelText("Log"), "Application");
    await user.selectOptions(screen.getByLabelText("Level"), "warnings");

    expect(api.eventRead).toHaveBeenLastCalledWith("Application", "warnings");
  });

  it("tracks tested keys without typing into the page", async () => {
    const user = userEvent.setup();
    render(<MonitorPage />);
    await user.click(screen.getByRole("tab", { name: "Hardware tests" }));

    const area = screen.getByRole("application", { name: "Keyboard test area" });
    fireEvent.keyDown(area, { code: "KeyA" });
    fireEvent.keyUp(area, { code: "KeyA" });

    expect(screen.getByTestId("key-KeyA").getAttribute("data-tested")).toBe("true");
    expect(screen.getByText(/1 of \d+ keys work/)).toBeTruthy();

    await user.click(screen.getByRole("button", { name: "Reset" }));
    expect(screen.getByText(/0 of \d+ keys work/)).toBeTruthy();
  });

  it("counts mouse buttons", async () => {
    const user = userEvent.setup();
    render(<MonitorPage />);
    await user.click(screen.getByRole("tab", { name: "Hardware tests" }));

    fireEvent.pointerDown(screen.getByTestId("mouse-pad"), { button: 2 });

    expect(screen.getByText("1 clicks")).toBeTruthy();
  });
});

describe("NetworkPage", () => {
  const status = {
    adapters: [{ name: "Ethernet", servers: ["192.168.1.1"] }],
    presets: [{ id: "google", title: "Google", note: "Fast.", primary: "8.8.8.8", secondary: "8.8.4.4" }],
    canRestore: false,
  };

  it("runs a ping and shows loss and jitter", async () => {
    const user = userEvent.setup();
    api.networkPing.mockResolvedValue({
      host: "1.1.1.1", address: "1.1.1.1", sent: 10, received: 9, minMs: 5, avgMs: 7, maxMs: 9, jitterMs: 1.5, lossPercent: 10,
    });
    render(<NetworkPage />);

    await user.click(screen.getByRole("button", { name: "Start" }));

    expect(await screen.findByText("7.0 ms")).toBeTruthy();
    expect(screen.getByText("1.5 ms")).toBeTruthy();
    expect(screen.getByText(/10% \(9 of 10 answered\)/)).toBeTruthy();
    expect(api.networkPing).toHaveBeenCalledWith("1.1.1.1");
  });

  it("asks for a port only for TCP ping", async () => {
    const user = userEvent.setup();
    api.networkTcpPing.mockRejectedValue(new Error("example couldn't be found."));
    render(<NetworkPage />);
    expect(screen.queryByLabelText("Port")).toBeNull();

    await user.selectOptions(screen.getByLabelText("Test"), "tcping");
    await user.click(screen.getByRole("button", { name: "Start" }));

    expect(api.networkTcpPing).toHaveBeenCalledWith("1.1.1.1", 443);
    expect(await screen.findByText("example couldn't be found.")).toBeTruthy();
  });

  it("shows every hop of a route test", async () => {
    const user = userEvent.setup();
    api.networkTrace.mockResolvedValue({
      host: "1.1.1.1",
      address: "1.1.1.1",
      reached: true,
      hops: [
        { hop: 1, address: "192.168.1.1", sent: 5, received: 5, avgMs: 1, bestMs: 1, worstMs: 2, lossPercent: 0 },
        { hop: 2, address: null, sent: 5, received: 0, avgMs: null, bestMs: null, worstMs: null, lossPercent: 100 },
      ],
    });
    render(<NetworkPage />);

    await user.selectOptions(screen.getByLabelText("Test"), "trace");
    await user.click(screen.getByRole("button", { name: "Start" }));

    expect(await screen.findByText("Reached 1.1.1.1 in 2 hops")).toBeTruthy();
    expect(screen.getByText("No answer")).toBeTruthy();
  });

  async function openDns() {
    const user = userEvent.setup();
    render(<NetworkPage />);
    await user.click(screen.getByRole("tab", { name: "DNS" }));
    await screen.findByText("Current DNS");
    return user;
  }

  it("switches provider and reloads", async () => {
    api.dnsStatus.mockResolvedValue(status);
    api.dnsSet.mockResolvedValue({ ok: true, message: "DNS was changed.", restartNeeded: false });
    const user = await openDns();

    expect(screen.queryByRole("button", { name: "Restore earlier DNS" })).toBeNull();
    await user.click(screen.getByRole("button", { name: "Switch to Google" }));

    expect(api.dnsSet).toHaveBeenCalledWith({ mode: "preset", presetId: "google" });
    expect(await screen.findByText("DNS was changed.")).toBeTruthy();
    await waitFor(() => expect(api.dnsStatus).toHaveBeenCalledTimes(2));
  });

  it("restores the earlier DNS when there is one", async () => {
    api.dnsStatus.mockResolvedValue({ ...status, canRestore: true });
    api.dnsSet.mockResolvedValue({ ok: true, message: "Your earlier DNS is back.", restartNeeded: false });
    const user = await openDns();

    await user.click(screen.getByRole("button", { name: "Restore earlier DNS" }));

    expect(api.dnsSet).toHaveBeenCalledWith({ mode: "previous" });
  });

  it("ranks the speed test and doesn't offer the current DNS", async () => {
    api.dnsStatus.mockResolvedValue(status);
    api.dnsBenchmark.mockResolvedValue([
      { id: "current", title: "Your current DNS", server: "192.168.1.1", medianMs: 3, failed: 0 },
      { id: "google", title: "Google", server: "8.8.8.8", medianMs: 20, failed: 1 },
      { id: "quad9", title: "Quad9", server: "9.9.9.9", medianMs: null, failed: 5 },
    ]);
    const user = await openDns();

    await user.click(screen.getByRole("button", { name: "Run speed test" }));

    expect(await screen.findByText("Your current DNS")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Use Your current DNS" })).toBeNull();
    expect(screen.getByRole("button", { name: "Use Google" })).toBeTruthy();
    expect(screen.getByText("20.0 ms, 1 failed")).toBeTruthy();
    expect(screen.getByText("No answer")).toBeTruthy();
  });

  it("sends a custom server and clears the cache", async () => {
    api.dnsStatus.mockResolvedValue(status);
    api.dnsSet.mockResolvedValue({ ok: true, message: "DNS was changed.", restartNeeded: false });
    api.dnsFlush.mockResolvedValue({ ok: true, message: "The DNS cache was cleared.", restartNeeded: false });
    const user = await openDns();

    await user.type(screen.getByLabelText("Primary DNS server"), "4.4.4.4");
    await user.click(screen.getByRole("button", { name: "Apply" }));
    expect(api.dnsSet).toHaveBeenCalledWith({ mode: "custom", primary: "4.4.4.4", secondary: undefined });

    await user.click(screen.getByRole("button", { name: "Clear DNS cache" }));
    expect(await screen.findByText("The DNS cache was cleared.")).toBeTruthy();
  });
});
