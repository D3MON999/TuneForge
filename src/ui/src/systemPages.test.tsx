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
    builtInList: vi.fn(),
    builtInRemove: vi.fn(),
    builtInReinstall: vi.fn(),
    softwareList: vi.fn(),
    softwareInstall: vi.fn(),
    programsList: vi.fn(),
    programsUpdate: vi.fn(),
    programsUninstall: vi.fn(),
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

describe("Built-in apps", () => {
  const apps = [
    { id: "a.news", title: "News", note: "n", group: "Microsoft", safety: "recommended" as const, installed: true, canReinstall: true },
    { id: "a.paint", title: "Paint", note: "n", group: "Microsoft", safety: "optional" as const, installed: true, canReinstall: false },
    { id: "a.help", title: "Get Help", note: "n", group: "Microsoft", safety: "caution" as const, installed: true, canReinstall: true },
    { id: "a.mail", title: "Mail", note: "n", group: "Microsoft", safety: "optional" as const, installed: false, canReinstall: true },
  ];

  async function open() {
    const user = userEvent.setup();
    api.builtInList.mockResolvedValue(apps);
    render(<AppsPage />);
    await user.click(screen.getByRole("tab", { name: "Built-in apps" }));
    await screen.findByText("News");
    return user;
  }

  it("selects only recommended apps and asks before removing", async () => {
    api.builtInRemove.mockResolvedValue({ removed: ["a.news"], failed: [] });
    const user = await open();

    await user.click(screen.getByRole("button", { name: "Select recommended" }));
    expect((screen.getByLabelText(/Paint/) as HTMLInputElement).checked).toBe(false);
    expect((screen.getByLabelText(/Get Help/) as HTMLInputElement).checked).toBe(false);

    await user.click(screen.getByRole("button", { name: "Remove 1 apps" }));
    expect(api.builtInRemove).not.toHaveBeenCalled();

    await user.click(screen.getByRole("button", { name: "Remove apps" }));
    expect(api.builtInRemove).toHaveBeenCalledWith(["a.news"]);
    expect(await screen.findByText("Removed 1 apps.")).toBeTruthy();
    await waitFor(() => expect(api.builtInList).toHaveBeenCalledTimes(2));
  });

  it("warns about risky apps and names any failures", async () => {
    api.builtInRemove.mockResolvedValue({ removed: [], failed: ["a.help"] });
    const user = await open();

    await user.click(screen.getByLabelText(/Get Help/));
    await user.click(screen.getByRole("button", { name: "Remove 1 apps" }));
    expect(screen.getByText(/These can break Windows features: Get Help/)).toBeTruthy();

    await user.click(screen.getByRole("button", { name: "Remove apps" }));
    expect(await screen.findByText(/Windows wouldn't remove: Get Help/)).toBeTruthy();
  });

  it("reinstalls a removed app", async () => {
    api.builtInReinstall.mockResolvedValue({ ok: true, message: "Mail was installed.", restartNeeded: false });
    const user = await open();

    await user.click(screen.getByRole("button", { name: "Reinstall Mail" }));

    expect(api.builtInReinstall).toHaveBeenCalledWith("a.mail");
    expect(await screen.findByText("Mail was installed.")).toBeTruthy();
  });
});

describe("Install apps", () => {
  const items = [
    { id: "x.seven", title: "7-Zip", note: "Archiver", category: "Utilities", installed: false },
    { id: "x.fox", title: "Firefox", note: "Browser", category: "Browsers", installed: false },
    { id: "x.vlc", title: "VLC", note: "Player", category: "Multimedia", installed: true },
  ];

  it("installs the selected apps one at a time and skips installed ones", async () => {
    const user = userEvent.setup();
    api.softwareList.mockResolvedValue({ wingetAvailable: true, items });
    api.softwareInstall.mockResolvedValueOnce({ ok: true, message: "7-Zip was installed.", restartNeeded: false });
    api.softwareInstall.mockRejectedValueOnce(new Error("failed"));
    render(<AppsPage />);

    await user.click(screen.getByRole("tab", { name: "Install apps" }));
    await user.click(await screen.findByLabelText(/7-Zip/));
    await user.click(screen.getByLabelText(/Firefox/));
    expect((screen.getByLabelText(/VLC/) as HTMLInputElement).disabled).toBe(true);

    await user.click(screen.getByRole("button", { name: "Install 2 apps" }));

    expect(await screen.findByText("7-Zip was installed.")).toBeTruthy();
    expect(await screen.findByText("Firefox: failed")).toBeTruthy();
    expect(api.softwareInstall.mock.calls).toEqual([["x.seven"], ["x.fox"]]);
  });

  it("filters by category and search", async () => {
    const user = userEvent.setup();
    api.softwareList.mockResolvedValue({ wingetAvailable: true, items });
    render(<AppsPage />);

    await user.click(screen.getByRole("tab", { name: "Install apps" }));
    await user.selectOptions(await screen.findByLabelText("Category"), "Browsers");
    expect(screen.queryByText("7-Zip")).toBeNull();

    await user.selectOptions(screen.getByLabelText("Category"), "");
    await user.type(screen.getByLabelText("Search apps to install"), "player");
    expect(screen.queryByText("Firefox")).toBeNull();
    expect(screen.getByText("VLC")).toBeTruthy();
  });

  it("explains when winget is missing", async () => {
    const user = userEvent.setup();
    api.softwareList.mockResolvedValue({ wingetAvailable: false, items: [] });
    render(<AppsPage />);

    await user.click(screen.getByRole("tab", { name: "Install apps" }));

    expect(await screen.findByText(/winget isn't on this PC/)).toBeTruthy();
  });
});

describe("Installed apps", () => {
  const items = [
    { name: "7-Zip", id: "x.seven", version: "24", available: "25", locked: false },
    { name: "Discord", id: "x.discord", version: "1", available: "2", locked: false },
    { name: "WebView2", id: "x.web", version: "1", available: null, locked: true },
  ];

  async function open() {
    const user = userEvent.setup();
    api.programsList.mockResolvedValue({ wingetAvailable: true, items });
    render(<AppsPage />);
    await user.click(screen.getByRole("tab", { name: "Installed apps" }));
    await screen.findByText("7-Zip");
    return user;
  }

  it("updates everything with an update", async () => {
    api.programsUpdate.mockResolvedValue({ ok: true, message: "Updated.", restartNeeded: false });
    const user = await open();

    await user.click(screen.getByRole("button", { name: "Update all (2)" }));

    await waitFor(() => expect(api.programsUpdate.mock.calls).toEqual([["x.seven"], ["x.discord"]]));
    await waitFor(() => expect(api.programsList).toHaveBeenCalledTimes(2));
  });

  it("asks before uninstalling and locks what the app needs", async () => {
    api.programsUninstall.mockResolvedValue({ ok: true, message: "Discord was uninstalled.", restartNeeded: false });
    const user = await open();

    expect(screen.queryByRole("button", { name: "Uninstall WebView2" })).toBeNull();
    expect(screen.getByText("Locked")).toBeTruthy();

    await user.click(screen.getByRole("button", { name: "Uninstall Discord" }));
    expect(api.programsUninstall).not.toHaveBeenCalled();

    await user.click(screen.getByRole("button", { name: "Yes, uninstall" }));
    expect(api.programsUninstall).toHaveBeenCalledWith("x.discord");
    expect(await screen.findByText("Discord was uninstalled.")).toBeTruthy();
  });
});
