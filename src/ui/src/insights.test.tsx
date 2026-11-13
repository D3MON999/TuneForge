// @vitest-environment happy-dom
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { bridge, type SpaceReport } from "./bridge";
import { HistoryPage } from "./HistoryPage";
import { DriverCard, OldDownloads, SpaceFinder, StartupCard } from "./Insights";

vi.mock("./bridge", () => ({
  bridge: {
    storageRead: vi.fn(),
    spaceScan: vi.fn(),
    spaceShow: vi.fn(),
    driverRead: vi.fn(),
    driverPage: vi.fn(),
    driverPackages: vi.fn(),
    driverRemove: vi.fn(),
    driverRestore: vi.fn(),
    driverDeleteBackup: vi.fn(),
    bootTimes: vi.fn(),
    history: vi.fn(),
    undo: vi.fn(),
    undoAll: vi.fn(),
    oldDownloads: vi.fn(),
    recycleDownloads: vi.fn(),
  },
}));

const api = vi.mocked(bridge);
const gb = 1024 ** 3;

const drive: SpaceReport = {
  path: "C:\\",
  bytes: 400 * gb,
  items: [
    { name: "Games", path: "C:\\Games", bytes: 300 * gb, isFolder: true },
    { name: "pagefile.sys", path: "C:\\pagefile.sys", bytes: 18 * gb, isFolder: false },
  ],
  largestFiles: [],
};

const games: SpaceReport = {
  path: "C:\\Games",
  bytes: 300 * gb,
  items: [{ name: "Big Game", path: "C:\\Games\\Big Game", bytes: 300 * gb, isFolder: true }],
  largestFiles: [],
};

beforeEach(() => {
  vi.resetAllMocks();
  api.driverPackages.mockResolvedValue({ packages: [], backups: [] });
  api.storageRead.mockResolvedValue({ disks: [], volumes: [{ name: "C:\\", label: "Windows", totalBytes: 500 * gb, freeBytes: 100 * gb }] });
});

afterEach(cleanup);

describe("Space finder", () => {
  it("waits for Scan, then lists what's biggest", async () => {
    const user = userEvent.setup();
    api.spaceScan.mockResolvedValue(drive);
    render(<SpaceFinder />);

    await screen.findByRole("option", { name: /C:/ });
    expect(api.spaceScan).not.toHaveBeenCalled();

    await user.click(screen.getByRole("button", { name: "Scan" }));

    expect(api.spaceScan).toHaveBeenCalledWith("C:\\");
    expect(await screen.findByRole("button", { name: "Games" })).toBeTruthy();
    expect(screen.getByText("pagefile.sys")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Scan again" })).toBeTruthy();
  });

  it("opens a folder and comes back without scanning again", async () => {
    const user = userEvent.setup();
    api.spaceScan.mockImplementation(async (path) => (path === "C:\\" ? drive : games));
    render(<SpaceFinder />);
    await screen.findByRole("option", { name: /C:/ });
    await user.click(screen.getByRole("button", { name: "Scan" }));

    await user.click(await screen.findByRole("button", { name: "Games" }));
    expect(await screen.findByRole("button", { name: "Big Game" })).toBeTruthy();

    await user.click(screen.getByRole("button", { name: "Back" }));
    expect(await screen.findByRole("button", { name: "Games" })).toBeTruthy();
    expect(api.spaceScan).toHaveBeenCalledTimes(2);
  });

  it("shows any item in File Explorer", async () => {
    const user = userEvent.setup();
    api.spaceScan.mockResolvedValue(drive);
    api.spaceShow.mockResolvedValue(true);
    render(<SpaceFinder />);
    await screen.findByRole("option", { name: /C:/ });
    await user.click(screen.getByRole("button", { name: "Scan" }));

    await user.click(await screen.findByRole("button", { name: "Show pagefile.sys in File Explorer" }));

    expect(api.spaceShow).toHaveBeenCalledWith("C:\\pagefile.sys");
  });

  it("ignores a scan that a newer one replaced", async () => {
    const user = userEvent.setup();
    api.spaceScan.mockResolvedValue(null);
    render(<SpaceFinder />);
    await screen.findByRole("option", { name: /C:/ });

    await user.click(screen.getByRole("button", { name: "Scan" }));

    await waitFor(() => expect(screen.getByRole("button", { name: "Scan" })).toBeTruthy());
    expect(screen.queryByText("pagefile.sys")).toBeNull();
  });
});

describe("Startup time", () => {
  it("only asks Windows when you press the button", async () => {
    const user = userEvent.setup();
    api.bootTimes.mockResolvedValue({
      boots: [
        { whenUtc: "2026-11-07T10:00:00Z", totalMs: 30000, mainPathMs: 12000, afterSignInMs: 18000 },
        { whenUtc: "2026-11-01T10:00:00Z", totalMs: 40000, mainPathMs: 15000, afterSignInMs: 25000 },
      ],
      firstChangeUtc: "2026-11-05T00:00:00Z",
    });
    render(<StartupCard />);

    expect(api.bootTimes).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "Check startup time" }));

    expect(await screen.findByText("30.0 s")).toBeTruthy();
    expect(screen.getByText(/Before your first TuneForge change: 40.0 s on average\. Since: 30.0 s\./)).toBeTruthy();
  });

  it("says so when Windows has no records", async () => {
    const user = userEvent.setup();
    api.bootTimes.mockResolvedValue({ boots: [], firstChangeUtc: null });
    render(<StartupCard />);

    await user.click(screen.getByRole("button", { name: "Check startup time" }));

    expect(await screen.findByText("Windows hasn't recorded any startup times on this PC.")).toBeTruthy();
  });
});

describe("Slow startup apps", () => {
  it("lists the apps Windows caught slowing startup", async () => {
    const user = userEvent.setup();
    api.bootTimes.mockResolvedValue({
      boots: [{ whenUtc: "2026-11-07T10:00:00Z", totalMs: 30000, mainPathMs: 12000, afterSignInMs: 18000 }],
      firstChangeUtc: null,
      slowApps: [{ name: "Chat app", file: "chat.exe", times: 4, addedMs: 3200 }],
    });
    render(<StartupCard />);

    await user.click(screen.getByRole("button", { name: "Check startup time" }));

    expect(await screen.findByText("Chat app")).toBeTruthy();
    expect(screen.getByText("+3.2 s, 4 times")).toBeTruthy();
  });
});

describe("Old downloads", () => {
  const report = {
    folder: "C:\\Users\\me\\Downloads",
    files: [
      { name: "big.iso", path: "C:\\Users\\me\\Downloads\\big.iso", bytes: 5 * gb, lastUsedUtc: "2026-01-01T00:00:00Z", ageDays: 300 },
      { name: "setup.exe", path: "C:\\Users\\me\\Downloads\\setup.exe", bytes: gb, lastUsedUtc: "2026-05-01T00:00:00Z", ageDays: 190 },
    ],
  };

  it("removes only what you picked, after asking", async () => {
    const user = userEvent.setup();
    api.oldDownloads.mockResolvedValueOnce(report).mockResolvedValueOnce({ ...report, files: report.files.slice(1) });
    api.recycleDownloads.mockResolvedValue({ moved: 1, failed: 0 });
    render(<OldDownloads />);

    await user.click(screen.getByRole("button", { name: "Find old downloads" }));
    const pick = await screen.findByRole("checkbox", { name: "Pick big.iso" });
    await user.click(pick);
    expect((pick as HTMLInputElement).checked).toBe(true);

    await user.click(screen.getByRole("button", { name: /Remove 1 picked/ }));
    expect(api.recycleDownloads).not.toHaveBeenCalled();
    expect(screen.getByText(/Move 1 file \(5\.0 GB\) to the Recycle Bin\?/)).toBeTruthy();

    await user.click(screen.getByRole("button", { name: "Move to Recycle Bin" }));

    expect(api.recycleDownloads).toHaveBeenCalledWith(["C:\\Users\\me\\Downloads\\big.iso"]);
    expect(await screen.findByText("1 file moved to the Recycle Bin.")).toBeTruthy();
    expect(screen.queryByText("big.iso")).toBeNull();
  });

  it("says when there's nothing old", async () => {
    const user = userEvent.setup();
    api.oldDownloads.mockResolvedValue({ folder: "C:\\Users\\me\\Downloads", files: [] });
    render(<OldDownloads />);

    await user.click(screen.getByRole("button", { name: "Find old downloads" }));

    expect(await screen.findByText(/No big, old files/)).toBeTruthy();
  });
});

describe("Graphics driver", () => {
  it("shows the version and age, and opens the maker's page", async () => {
    const user = userEvent.setup();
    api.driverRead.mockResolvedValue([
      { name: "NVIDIA GeForce RTX 3060", vendor: "NVIDIA", version: "32.0.16.1714", friendlyVersion: "617.14", dateUtc: "2026-09-17T00:00:00Z", ageDays: 51, hasDownloadPage: true },
    ]);
    api.driverPage.mockResolvedValue(true);
    render(<DriverCard />);

    expect(await screen.findByText("617.14")).toBeTruthy();
    expect(screen.getByText(/51 days old/)).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Check NVIDIA for a newer one" }));
    expect(api.driverPage).toHaveBeenCalledWith("NVIDIA");
  });

  it("removes a driver only after you confirm, then offers the maker's page", async () => {
    const user = userEvent.setup();
    api.driverRead.mockResolvedValue([]);
    api.driverPackages
      .mockResolvedValueOnce({ packages: [{ inf: "oem52.inf", vendor: "NVIDIA", version: "32.0.16.2114" }, { inf: "oem54.inf", vendor: "NVIDIA", version: "32.0.16.2124" }], backups: [] })
      .mockResolvedValue({ packages: [], backups: [{ vendor: "NVIDIA", bytes: 1.4 * gb }] });
    api.driverRemove.mockResolvedValue({ ok: true, message: "Removed 2 driver packages for NVIDIA.", restartNeeded: true });
    api.driverPage.mockResolvedValue(true);
    render(<DriverCard />);

    await user.click(await screen.findByRole("button", { name: "Remove NVIDIA driver" }));
    expect(api.driverRemove).not.toHaveBeenCalled();
    expect(screen.getByText(/Removes 2 driver packages, including older leftovers/)).toBeTruthy();

    await user.click(screen.getByRole("button", { name: "Yes, remove it" }));

    expect(api.driverRemove).toHaveBeenCalledWith("NVIDIA");
    expect(await screen.findByText("Removed 2 driver packages for NVIDIA.")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Get the new NVIDIA driver" }));
    expect(api.driverPage).toHaveBeenCalledWith("NVIDIA");
    expect(await screen.findByRole("button", { name: "Put it back" })).toBeTruthy();
  });

  it("puts a backup back, and asks before deleting it", async () => {
    const user = userEvent.setup();
    api.driverRead.mockResolvedValue([]);
    api.driverPackages.mockResolvedValue({ packages: [], backups: [{ vendor: "AMD", bytes: gb }] });
    api.driverRestore.mockResolvedValue({ ok: true, message: "The backed-up AMD driver is back. Restart to finish.", restartNeeded: true });
    api.driverDeleteBackup.mockResolvedValue({ ok: true, message: "The AMD driver backup was deleted.", restartNeeded: false });
    render(<DriverCard />);

    await user.click(await screen.findByRole("button", { name: "Put it back" }));
    expect(api.driverRestore).toHaveBeenCalledWith("AMD");
    expect(await screen.findByText(/AMD driver is back/)).toBeTruthy();

    await user.click(screen.getByRole("button", { name: "Delete backup" }));
    expect(api.driverDeleteBackup).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "Yes, delete it" }));
    expect(api.driverDeleteBackup).toHaveBeenCalledWith("AMD");
  });

  it("warns when a driver is old", async () => {
    api.driverRead.mockResolvedValue([
      { name: "Radeon", vendor: "AMD", version: "31.0.1", friendlyVersion: null, dateUtc: "2025-01-01T00:00:00Z", ageDays: 400, hasDownloadPage: true },
    ]);
    render(<DriverCard />);

    expect(await screen.findByText(/A newer one is likely out\./)).toBeTruthy();
  });
});

describe("Undo everything", () => {
  const sets = [
    { id: 2, createdUtc: "2026-11-07T10:00:00Z", restorePoint: "skipped", undoneUtc: null, tweakIds: ["a"] },
    { id: 1, createdUtc: "2026-11-06T10:00:00Z", restorePoint: "skipped", undoneUtc: null, tweakIds: ["b", "c"] },
  ];

  it("asks first, then undoes everything in one go", async () => {
    const user = userEvent.setup();
    api.history.mockResolvedValue(sets);
    api.undoAll.mockResolvedValue({ changeSetId: 0, restored: 3, problems: [] });
    render(<HistoryPage />);

    await user.click(await screen.findByRole("button", { name: "Undo everything" }));
    expect(api.undoAll).not.toHaveBeenCalled();
    expect(screen.getByText(/Undo all 2 changes\? This also puts back any locks you removed\./)).toBeTruthy();

    await user.click(screen.getByRole("button", { name: "Yes, undo everything" }));

    expect(api.undoAll).toHaveBeenCalledTimes(1);
    expect(await screen.findByText("Restored 3 settings.")).toBeTruthy();
  });

  it("isn't offered when there's nothing to undo", async () => {
    api.history.mockResolvedValue([{ ...sets[0], undoneUtc: "2026-11-07T11:00:00Z" }]);
    render(<HistoryPage />);

    await screen.findByText(/undone/);
    expect(screen.queryByRole("button", { name: "Undo everything" })).toBeNull();
  });
});
