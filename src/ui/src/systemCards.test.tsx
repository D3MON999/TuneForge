// @vitest-environment happy-dom
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { bridge } from "./bridge";
import { DriveWearCard, DriverBackupCard, FirewallCard, MenuTab, PauseUpdatesCard, SignatureCard, TasksTab, WakeCard, wearVerdict } from "./SystemCards";

vi.mock("./bridge", () => ({
  bridge: {
    driveWear: vi.fn(),
    wakeRead: vi.fn(),
    sleepBlockers: vi.fn(),
    wakeDevice: vi.fn(),
    tasksList: vi.fn(),
    taskSet: vi.fn(),
    menuList: vi.fn(),
    menuSet: vi.fn(),
    processSignatures: vi.fn(),
    firewallList: vi.fn(),
    firewallPick: vi.fn(),
    firewallBlock: vi.fn(),
    firewallUnblock: vi.fn(),
    pauseStatus: vi.fn(),
    pauseUpdates: vi.fn(),
    driversExport: vi.fn(),
    driversPickFolder: vi.fn(),
    driversImport: vi.fn(),
    spaceShow: vi.fn(),
  },
}));

const api = vi.mocked(bridge);
const ok = (message: string) => ({ ok: true, message, restartNeeded: false });

beforeEach(() => {
  vi.resetAllMocks();
});

afterEach(cleanup);

describe("Monitor cards", () => {
  it("reads drive wear only when asked", async () => {
    const user = userEvent.setup();
    api.driveWear.mockResolvedValue([{ name: "Samsung 980", media: "NVMe SSD", wearPercent: 12, temperatureC: 41, powerOnHours: 5210, readErrors: 0 }]);
    render(<DriveWearCard />);

    expect(api.driveWear).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "Check drive wear" }));

    expect(await screen.findByText("12%")).toBeTruthy();
    expect(screen.getByText("Plenty of life left.")).toBeTruthy();
  });

  it("judges wear in plain words", () => {
    expect(wearVerdict({ name: "a", media: "SSD", wearPercent: 75 })?.text).toMatch(/Well used/);
    expect(wearVerdict({ name: "a", media: "SSD", wearPercent: 95 })?.text).toMatch(/Nearly worn out/);
    expect(wearVerdict({ name: "a", media: "SSD" })).toBeNull();
  });

  it("shows wakes, lets a device stop waking the PC, and lists what keeps it awake", async () => {
    const user = userEvent.setup();
    api.wakeRead.mockResolvedValue({
      wakes: [{ whenUtc: "2026-11-22T03:10:00Z", source: "Power button" }],
      devices: [{ name: "HID-compliant mouse", armed: true }, { name: "Keyboard", armed: false }],
    });
    api.wakeDevice.mockResolvedValue(ok("HID-compliant mouse won't wake the PC anymore."));
    api.sleepBlockers.mockResolvedValue([{ kind: "system", who: "Spotify.exe", detail: "Playing audio" }]);
    render(<WakeCard />);

    expect(await screen.findByText("Power button")).toBeTruthy();
    expect(screen.queryByText("Keyboard")).toBeNull();
    await user.click(screen.getByRole("checkbox", { name: "HID-compliant mouse can wake the PC" }));
    expect(api.wakeDevice).toHaveBeenCalledWith("HID-compliant mouse", false);

    await user.click(screen.getByRole("button", { name: "Check what's keeping it awake" }));
    expect(await screen.findByText("Spotify.exe")).toBeTruthy();
    expect(screen.getByText(/Keeps the PC awake: Playing audio/)).toBeTruthy();
  });
});

describe("Apps tabs", () => {
  it("turns a scheduled task off, but not a locked one", async () => {
    const user = userEvent.setup();
    api.tasksList.mockResolvedValue([
      { path: "\\Updater", name: "Updater", program: "C:\\App\\update.exe", enabled: true, locked: false },
      { path: "\\Riot", name: "Riot Client", program: "C:\\Riot\\a.exe", enabled: true, locked: true },
    ]);
    api.taskSet.mockResolvedValue(ok("Updater is off."));
    render(<TasksTab />);

    await user.click(await screen.findByRole("checkbox", { name: "Updater runs on schedule" }));
    expect(api.taskSet).toHaveBeenCalledWith("\\Updater", false);
    expect((screen.getByRole("checkbox", { name: "Riot Client runs on schedule" }) as HTMLInputElement).disabled).toBe(true);
  });

  it("hides a right-click menu entry", async () => {
    const user = userEvent.setup();
    api.menuList.mockResolvedValue([{ id: "verb:HKLM:Directory:VLC", name: "Add to VLC", where: "folders", file: "C:\\vlc.exe", enabled: true }]);
    api.menuSet.mockResolvedValue(ok("Saved. You can undo this from History."));
    render(<MenuTab />);

    expect(await screen.findByText("On folders")).toBeTruthy();
    await user.click(screen.getByRole("checkbox", { name: "Show Add to VLC in the menu" }));
    expect(api.menuSet).toHaveBeenCalledWith("verb:HKLM:Directory:VLC", false);
  });

  it("puts unsigned programs first", async () => {
    const user = userEvent.setup();
    api.processSignatures.mockResolvedValue([
      { name: "tool", path: "C:\\tool.exe", status: "unsigned" },
      { name: "Steam", path: "C:\\steam.exe", status: "signed", publisher: "Valve Corp." },
    ]);
    render(<SignatureCard />);

    await user.click(screen.getByRole("button", { name: "Check signatures" }));
    expect(await screen.findByText("Not signed (1)")).toBeTruthy();
    expect(screen.getByText("Valve Corp.")).toBeTruthy();
  });
});

describe("Network, Optimize and Repair cards", () => {
  it("blocks a picked program and can unblock it", async () => {
    const user = userEvent.setup();
    api.firewallList.mockResolvedValueOnce([]).mockResolvedValue([{ program: "C:\\Games\\launcher.exe" }]);
    api.firewallPick.mockResolvedValue("C:\\Games\\launcher.exe");
    api.firewallBlock.mockResolvedValue(ok("launcher.exe can't reach the internet now."));
    api.firewallUnblock.mockResolvedValue(ok("launcher.exe can reach the internet again."));
    render(<FirewallCard />);

    await user.click(await screen.findByRole("button", { name: "Pick a program to block" }));
    expect(api.firewallBlock).toHaveBeenCalledWith("C:\\Games\\launcher.exe");
    await user.click(await screen.findByRole("button", { name: "Unblock" }));
    expect(api.firewallUnblock).toHaveBeenCalledWith("C:\\Games\\launcher.exe");
  });

  it("does nothing when no program is picked", async () => {
    const user = userEvent.setup();
    api.firewallList.mockResolvedValue([]);
    api.firewallPick.mockResolvedValue(null);
    render(<FirewallCard />);

    await user.click(await screen.findByRole("button", { name: "Pick a program to block" }));
    expect(api.firewallBlock).not.toHaveBeenCalled();
  });

  it("pauses updates for the chosen weeks and can resume", async () => {
    const user = userEvent.setup();
    api.pauseStatus.mockResolvedValueOnce({ paused: false }).mockResolvedValue({ paused: true, untilUtc: "2026-12-14T00:00:00Z" });
    api.pauseUpdates.mockResolvedValue(ok("Updates are paused for 3 weeks."));
    render(<PauseUpdatesCard />);

    await user.selectOptions(await screen.findByLabelText("How long to pause"), "3");
    await user.click(screen.getByRole("button", { name: "Pause updates" }));
    expect(api.pauseUpdates).toHaveBeenCalledWith(3);
    await user.click(await screen.findByRole("button", { name: "Resume updates" }));
    expect(api.pauseUpdates).toHaveBeenLastCalledWith(0);
  });

  it("backs up drivers and offers the folder", async () => {
    const user = userEvent.setup();
    api.driversExport.mockResolvedValue({ ok: true, message: "Saved 84 drivers.", folder: "C:\\Users\\me\\Documents\\TuneForge Backups\\Drivers" });
    api.spaceShow.mockResolvedValue(true);
    render(<DriverBackupCard />);

    await user.click(screen.getByRole("button", { name: "Back up drivers" }));
    expect(await screen.findByText("Saved 84 drivers.")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Show folder" }));
    expect(api.spaceShow).toHaveBeenCalledWith("C:\\Users\\me\\Documents\\TuneForge Backups\\Drivers");
  });
});
