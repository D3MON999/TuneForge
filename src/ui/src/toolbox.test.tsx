// @vitest-environment happy-dom
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { bridge } from "./bridge";
import {
  ChangesCard,
  ConnectionLogCard,
  CrashCard,
  DuplicatesCard,
  HealthTrend,
  HostsCard,
  IconCacheCard,
  LeftoversPanel,
  PermissionsCard,
  quietestChannel,
  RestorePointsCard,
  speakerNames,
  speedVerdict,
} from "./Toolbox";

vi.mock("./bridge", () => ({
  bridge: {
    crashList: vi.fn(),
    restorePoints: vi.fn(),
    restorePointDelete: vi.fn(),
    iconCacheRebuild: vi.fn(),
    connectionLog: vi.fn(),
    connectionLogSet: vi.fn(),
    connectionLogOpen: vi.fn(),
    hostsRead: vi.fn(),
    hostsRemove: vi.fn(),
    hostsRestore: vi.fn(),
    storageRead: vi.fn(),
    duplicatesScan: vi.fn(),
    duplicatesRecycle: vi.fn(),
    leftoversFind: vi.fn(),
    leftoversRecycle: vi.fn(),
    spaceShow: vi.fn(),
    healthHistory: vi.fn(),
    changesRead: vi.fn(),
    permissionsRead: vi.fn(),
    settingsPage: vi.fn(),
  },
}));

const api = vi.mocked(bridge);
const mb = 1024 * 1024;

beforeEach(() => {
  vi.resetAllMocks();
  api.storageRead.mockResolvedValue({ disks: [], volumes: [{ name: "C:\\", label: "Windows", totalBytes: 1, freeBytes: 1 }, { name: "D:\\", label: "Games", totalBytes: 1, freeBytes: 1 }] });
});

afterEach(cleanup);

describe("Repair cards", () => {
  it("explains each crash", async () => {
    api.crashList.mockResolvedValue([
      { whenUtc: "2026-11-10T20:00:00Z", kind: "bluescreen", code: "0x00000116", name: "VIDEO TDR FAILURE", meaning: "The graphics card stopped responding.", dumpPath: "C:\\WINDOWS\\Minidump\\a.dmp" },
    ]);
    render(<CrashCard />);

    expect(await screen.findByText("VIDEO TDR FAILURE")).toBeTruthy();
    expect(screen.getByText("0x00000116")).toBeTruthy();
    expect(screen.getByText(/Minidump/)).toBeTruthy();
  });

  it("says when there were no crashes", async () => {
    api.crashList.mockResolvedValue([]);
    render(<CrashCard />);
    expect(await screen.findByText(/No blue screens/)).toBeTruthy();
  });

  it("asks before deleting a restore point", async () => {
    const user = userEvent.setup();
    const report = { points: [{ sequence: 7, description: "Before driver", createdUtc: "2026-11-01T00:00:00Z" }], usedBytes: 2e9, maxBytes: 10e9 };
    api.restorePoints.mockResolvedValue(report);
    api.restorePointDelete.mockResolvedValue({ ok: true, message: "The restore point was deleted.", restartNeeded: false });
    render(<RestorePointsCard />);

    expect(api.restorePoints).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "Show restore points" }));
    await user.click(await screen.findByRole("button", { name: "Delete Before driver" }));
    expect(api.restorePointDelete).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "Yes, delete" }));

    expect(api.restorePointDelete).toHaveBeenCalledWith(7);
    expect(await screen.findByText("The restore point was deleted.")).toBeTruthy();
  });

  it("warns before restarting Explorer", async () => {
    const user = userEvent.setup();
    api.iconCacheRebuild.mockResolvedValue({ ok: true, message: "Icons and thumbnails are rebuilding.", restartNeeded: false });
    render(<IconCacheCard />);

    await user.click(screen.getByRole("button", { name: "Rebuild icons" }));
    expect(api.iconCacheRebuild).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "Yes, rebuild" }));
    expect(await screen.findByText(/rebuilding/)).toBeTruthy();
  });
});

describe("Network cards", () => {
  it("starts the logger and tells a router problem from the internet's", async () => {
    const user = userEvent.setup();
    api.connectionLog.mockResolvedValue({ running: false, pings: 0, events: [], logPath: "log.csv" });
    api.connectionLogSet.mockResolvedValue({
      running: true,
      sinceUtc: "2026-11-20T20:00:00Z",
      pings: 400,
      router: "192.168.1.1",
      logPath: "log.csv",
      events: [{ whenUtc: "2026-11-20T21:00:00Z", where: "internet", kind: "drop", seconds: 12 }],
    });
    render(<ConnectionLogCard />);

    await user.click(await screen.findByRole("button", { name: "Start logging" }));

    expect(api.connectionLogSet).toHaveBeenCalledWith(true);
    expect(await screen.findByText("Internet dropped for 12 s")).toBeTruthy();
    expect(screen.getByText(/points to your internet provider/)).toBeTruthy();
  });

  it("removes picked hosts lines after asking", async () => {
    const user = userEvent.setup();
    api.hostsRead.mockResolvedValue({
      entries: [{ text: "127.0.0.1 ads.example.com", address: "127.0.0.1", names: ["ads.example.com"], note: "Ad blocker", blocks: true }],
      hasBackup: false,
    });
    api.hostsRemove.mockResolvedValue({ ok: true, message: "The line was removed.", restartNeeded: false });
    render(<HostsCard />);

    await user.click(await screen.findByRole("checkbox", { name: "Pick ads.example.com" }));
    await user.click(screen.getByRole("button", { name: "Remove 1 picked" }));
    expect(api.hostsRemove).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "Yes, remove" }));

    expect(api.hostsRemove).toHaveBeenCalledWith(["127.0.0.1 ads.example.com"]);
  });

  it("suggests the quietest of the non-overlapping channels", () => {
    expect(
      quietestChannel({
        hasWifi: true,
        networks: [
          { name: "Mine", channel: 1, band: "2.4 GHz", signalPercent: 90, connected: true },
          { name: "A", channel: 1, band: "2.4 GHz", signalPercent: 70, connected: false },
          { name: "B", channel: 6, band: "2.4 GHz", signalPercent: 60, connected: false },
          { name: "C", channel: 3, band: "2.4 GHz", signalPercent: 40, connected: false },
        ],
      }),
    ).toBe(11);
  });
});

describe("Testers helpers", () => {
  it("names a drive by how it performs", () => {
    expect(speedVerdict({ drive: "D:\\", readMBps: 128, writeMBps: 93, randomReadIops: 75 })).toMatch(/hard drive/);
    expect(speedVerdict({ drive: "C:\\", readMBps: 3400, writeMBps: 2900, randomReadIops: 15000 })).toMatch(/NVMe/);
    expect(speedVerdict({ drive: "E:\\", readMBps: 520, writeMBps: 480, randomReadIops: 9000 })).toMatch(/SATA/);
  });

  it("lists speakers in Windows' channel order", () => {
    expect(speakerNames(2)).toEqual(["Left", "Right"]);
    expect(speakerNames(6)[3]).toBe("Subwoofer");
    expect(speakerNames(8)).toHaveLength(8);
  });
});

describe("Cleanup and Apps cards", () => {
  const report = {
    wastedBytes: 50 * mb,
    groups: [
      {
        bytes: 50 * mb,
        files: [
          { path: "C:\\Users\\me\\Downloads\\a.iso", name: "a.iso", modifiedUtc: "2026-01-01T00:00:00Z" },
          { path: "C:\\Users\\me\\Desktop\\a.iso", name: "a.iso", modifiedUtc: "2026-02-01T00:00:00Z" },
        ],
      },
    ],
  };

  it("always keeps one copy of a duplicate", async () => {
    const user = userEvent.setup();
    api.duplicatesScan.mockResolvedValue(report);
    api.duplicatesRecycle.mockResolvedValue({ moved: 1, failed: 0 });
    render(<DuplicatesCard />);

    await user.click(screen.getByRole("button", { name: "Find duplicates" }));
    const first = await screen.findByRole("checkbox", { name: "Remove C:\\Users\\me\\Downloads\\a.iso" });
    const second = screen.getByRole("checkbox", { name: "Remove C:\\Users\\me\\Desktop\\a.iso" });
    await user.click(second);

    expect((second as HTMLInputElement).checked).toBe(true);
    expect((first as HTMLInputElement).disabled).toBe(true);

    await user.click(screen.getByRole("button", { name: /Remove 1 picked/ }));
    await user.click(screen.getByRole("button", { name: "Move to Recycle Bin" }));

    expect(api.duplicatesRecycle).toHaveBeenCalledWith([{ path: "C:\\Users\\me\\Desktop\\a.iso", keep: "C:\\Users\\me\\Downloads\\a.iso" }]);
  });

  it("offers only your own leftover folders for removal", async () => {
    const user = userEvent.setup();
    api.leftoversFind.mockResolvedValueOnce([
      { path: "C:\\Users\\me\\AppData\\Roaming\\obs-studio", bytes: 5 * mb, canRemove: true },
      { path: "C:\\ProgramData\\obs-studio", bytes: mb, canRemove: false },
    ]).mockResolvedValue([{ path: "C:\\ProgramData\\obs-studio", bytes: mb, canRemove: false }]);
    api.leftoversRecycle.mockResolvedValue({ moved: 1, failed: 0 });
    render(<LeftoversPanel name="OBS Studio" />);

    expect(await screen.findByText("OBS Studio left these folders behind")).toBeTruthy();
    expect(screen.getAllByRole("checkbox")).toHaveLength(1);
    await user.click(screen.getByRole("button", { name: "Move 1 to the Recycle Bin" }));

    expect(api.leftoversRecycle).toHaveBeenCalledWith("OBS Studio", ["C:\\Users\\me\\AppData\\Roaming\\obs-studio"]);
    expect(await screen.findByText("1 folder moved to the Recycle Bin.")).toBeTruthy();
  });
});

describe("Dashboard and Privacy cards", () => {
  it("summarizes the health trend", async () => {
    api.healthHistory.mockResolvedValue([
      { date: "2026-11-01", score: 90 },
      { date: "2026-11-10", score: 70 },
      { date: "2026-11-20", score: 85 },
    ]);
    render(<HealthTrend />);

    expect(await screen.findByText(/Down 5 points since/)).toBeTruthy();
    expect(screen.getByText(/Lowest: 70/)).toBeTruthy();
  });

  it("stays hidden until there are two days to compare", async () => {
    api.healthHistory.mockResolvedValue([{ date: "2026-11-20", score: 85 }]);
    const { container } = render(<HealthTrend />);
    await waitFor(() => expect(api.healthHistory).toHaveBeenCalled());
    expect(container.textContent).toBe("");
  });

  it("lists what changed", async () => {
    api.changesRead.mockResolvedValue({
      sinceDate: "2026-11-13",
      changes: [
        { category: "Programs", added: ["New Game"], removed: ["Old Tool"] },
        { category: "Services", added: [], removed: [] },
      ],
    });
    render(<ChangesCard />);

    expect(await screen.findByText("New Game")).toBeTruthy();
    expect(screen.getByText("Old Tool")).toBeTruthy();
    expect(screen.queryByText("Services")).toBeNull();
  });

  it("explains the first visit", async () => {
    api.changesRead.mockResolvedValue({ sinceDate: null, changes: [] });
    render(<ChangesCard />);
    expect(await screen.findByText(/Come back in a few days/)).toBeTruthy();
  });

  it("shows which apps used the camera and microphone", async () => {
    const user = userEvent.setup();
    api.permissionsRead.mockResolvedValue([
      { capability: "microphone", app: "Discord", lastUsedUtc: "2026-11-20T10:00:00Z", inUse: true },
      { capability: "camera", app: "Zoom", lastUsedUtc: "2026-11-02T10:00:00Z", inUse: false },
    ]);
    api.settingsPage.mockResolvedValue(true);
    render(<PermissionsCard />);

    expect(await screen.findByText("In use now")).toBeTruthy();
    expect(screen.getByText("Zoom")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Open camera settings" }));
    expect(api.settingsPage).toHaveBeenCalledWith("ms-settings:privacy-webcam");
  });
});
