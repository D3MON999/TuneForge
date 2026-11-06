// @vitest-environment happy-dom
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AutomationSettings, MemoryCard } from "./AutomationPanels";
import { bridge, type SettingsChoices } from "./bridge";

vi.mock("./bridge", () => ({
  bridge: { settingsGet: vi.fn(), settingsSave: vi.fn(), memoryClean: vi.fn() },
}));

const api = vi.mocked(bridge);

const defaults: SettingsChoices = {
  closeToTray: false,
  startWithWindows: false,
  notifications: true,
  memoryAuto: false,
  memoryThreshold: 85,
  cleanAuto: false,
  cleanDays: 7,
  startupWatch: false,
};

beforeEach(() => {
  vi.resetAllMocks();
  api.settingsGet.mockResolvedValue({ choices: defaults });
  api.settingsSave.mockImplementation(async (choices) => ({ choices }));
});

afterEach(cleanup);

describe("AutomationSettings", () => {
  it("starts with everything automatic off", async () => {
    render(<AutomationSettings />);

    expect(((await screen.findByLabelText(/Clean junk files/)) as HTMLInputElement).checked).toBe(false);
    expect((screen.getByLabelText(/Start with Windows/) as HTMLInputElement).checked).toBe(false);
    expect((screen.getByLabelText(/Free memory when/) as HTMLInputElement).checked).toBe(false);
  });

  it("saves a change and warns if automations can't keep running", async () => {
    const user = userEvent.setup();
    render(<AutomationSettings />);

    await user.click(await screen.findByLabelText(/Alert me about new startup/));

    expect(api.settingsSave).toHaveBeenCalledWith({ ...defaults, startupWatch: true });
    expect(await screen.findByText(/They stop when you close TuneForge/)).toBeTruthy();

    await user.click(screen.getByLabelText(/Keep running in the tray/));
    await waitFor(() => expect(screen.queryByText(/They stop when you close TuneForge/)).toBeNull());
  });

  it("shows a value only when its automation is on and saves it on leaving the box", async () => {
    const user = userEvent.setup();
    render(<AutomationSettings />);

    await screen.findByLabelText(/Clean junk files/);
    expect(screen.queryByLabelText("Days between cleans")).toBeNull();

    await user.click(screen.getByLabelText(/Clean junk files/));
    const days = await screen.findByLabelText("Days between cleans");
    await user.clear(days);
    await user.type(days, "14");
    await user.tab();

    await waitFor(() => expect(api.settingsSave).toHaveBeenLastCalledWith({ ...defaults, cleanAuto: true, cleanDays: 14 }));
  });

  it("shows a failed save", async () => {
    const user = userEvent.setup();
    api.settingsSave.mockRejectedValue(new Error("Couldn't save."));
    render(<AutomationSettings />);

    await user.click(await screen.findByLabelText(/Show notifications/));

    expect(await screen.findByText("Couldn't save.")).toBeTruthy();
  });
});

describe("MemoryCard", () => {
  it("reports before and after numbers", async () => {
    const user = userEvent.setup();
    api.memoryClean.mockResolvedValue({
      usedBeforeBytes: 3 * 1024 ** 3,
      usedAfterBytes: 2 * 1024 ** 3,
      totalBytes: 8 * 1024 ** 3,
      processes: 40,
      freedBytes: 1024 ** 3,
    });
    render(<MemoryCard />);

    await user.click(screen.getByRole("button", { name: "Free memory" }));

    expect(await screen.findByText(/Freed 1\.0 GB\. Memory in use went from 3\.0 GB to 2\.0 GB\./)).toBeTruthy();
    expect(screen.getByText(/won't raise FPS/)).toBeTruthy();
  });
});
