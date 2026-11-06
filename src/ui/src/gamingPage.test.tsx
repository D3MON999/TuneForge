// @vitest-environment happy-dom
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { bridge, type GameBoosts } from "./bridge";
import { GamingPage } from "./GamingPage";

vi.mock("./TweaksPage", () => ({ TweaksPage: () => <p>Gaming tweaks</p> }));
vi.mock("./bridge", () => ({
  bridge: {
    gamesList: vi.fn(),
    gamesSave: vi.fn(),
    gamingOverview: vi.fn(),
    powerSet: vi.fn(),
    powerUltimate: vi.fn(),
    focusSave: vi.fn(),
    powerCreate: vi.fn(),
    powerParking: vi.fn(),
    powerKeepCores: vi.fn(),
    displayRead: vi.fn(),
    displayBest: vi.fn(),
    turboGet: vi.fn(),
    turboSet: vi.fn(),
    gamesVerify: vi.fn(),
    biosRestart: vi.fn(),
    biosCancel: vi.fn(),
  },
}));

const api = vi.mocked(bridge);

const off: GameBoosts = {
  highPriority: false,
  highPerformanceGpu: false,
  disableFullscreenOptimizations: false,
  powerBoost: false,
  focus: false,
};

const games = [
  { id: "a", title: "Solo Game", launcher: "Steam", folder: "C:\\A", executable: "C:\\A\\a.exe", antiCheat: null, boosts: off },
  { id: "b", title: "Online Game", launcher: "Epic Games", folder: "C:\\B", executable: "C:\\B\\b.exe", antiCheat: "BattlEye", boosts: off },
];

const overview = {
  resizeBar: "off" as const,
  plans: [
    { id: "p1", name: "Balanced", active: true },
    { id: "p2", name: "High performance", active: false },
  ],
  focusApps: ["Discord"],
  runningApps: ["Discord", "Spotify"],
};

beforeEach(() => {
  vi.resetAllMocks();
  api.gamesList.mockResolvedValue(games);
  api.gamingOverview.mockResolvedValue(overview);
  api.displayRead.mockResolvedValue({ name: "2560 x 1440", currentHz: 60, bestHz: 165 });
  api.powerParking.mockResolvedValue({ minimumCoresPercent: 10 });
  api.turboGet.mockResolvedValue(false);
});

afterEach(cleanup);

describe("GamingPage", () => {
  it("lists games and saves a boost", async () => {
    const user = userEvent.setup();
    api.gamesSave.mockResolvedValue({ ok: true, message: "Saved.", restartNeeded: false });
    render(<GamingPage />);

    await user.click(await screen.findByRole("button", { name: "Boosts for Solo Game" }));
    await user.click(screen.getByLabelText(/High performance power/));

    expect(api.gamesSave).toHaveBeenCalledWith("a", { ...off, powerBoost: true });
    expect(await screen.findByText("Steam, 1 on")).toBeTruthy();
  });

  it("blocks high priority for games with anti-cheat", async () => {
    const user = userEvent.setup();
    render(<GamingPage />);

    await user.click(await screen.findByRole("button", { name: "Boosts for Online Game" }));

    expect((screen.getByLabelText(/High priority/) as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByLabelText(/fast graphics card/) as HTMLInputElement).disabled).toBe(false);
  });

  it("says when no games are found", async () => {
    api.gamesList.mockResolvedValue([]);
    render(<GamingPage />);

    expect(await screen.findByText(/No Steam, Epic or GOG games/)).toBeTruthy();
  });

  it("switches the power plan", async () => {
    const user = userEvent.setup();
    api.powerSet.mockResolvedValue({ ok: true, message: "The power plan was changed.", restartNeeded: false });
    render(<GamingPage />);

    await user.click(screen.getByRole("tab", { name: "Power and focus" }));
    await user.click(await screen.findByRole("button", { name: "Use High performance" }));

    expect(api.powerSet).toHaveBeenCalledWith("p2");
    expect(await screen.findByText("The power plan was changed.")).toBeTruthy();
    expect(screen.getByText(/Resize BAR is off/)).toBeTruthy();
  });

  it("offers the top refresh rate, core parking and a custom plan", async () => {
    const user = userEvent.setup();
    api.displayBest.mockResolvedValue({ ok: true, message: "Done.", restartNeeded: false });
    api.powerKeepCores.mockResolvedValue({ ok: true, message: "Done.", restartNeeded: false });
    api.powerCreate.mockResolvedValue({ ok: true, message: "Created.", restartNeeded: false });
    render(<GamingPage />);

    await user.click(screen.getByRole("tab", { name: "Power and focus" }));
    await user.click(await screen.findByRole("button", { name: "Use 165 Hz" }));
    expect(api.displayBest).toHaveBeenCalled();

    await user.click(screen.getByRole("button", { name: "Keep cores awake" }));
    expect(api.powerKeepCores).toHaveBeenCalled();

    await user.type(screen.getByLabelText("New plan name"), "Mine");
    await user.click(screen.getByRole("button", { name: "Create from current plan" }));
    expect(api.powerCreate).toHaveBeenCalledWith("Mine");
  });

  it("turns Turbo mode on and asks before restarting into BIOS", async () => {
    const user = userEvent.setup();
    api.turboSet.mockResolvedValue(true);
    api.biosRestart.mockResolvedValue({ ok: true, message: "Restarting.", restartNeeded: true });
    render(<GamingPage />);

    await user.click(await screen.findByRole("button", { name: "Turn on" }));
    expect(api.turboSet).toHaveBeenCalledWith(true);
    expect(await screen.findByText(/Turbo mode is on/)).toBeTruthy();

    await user.click(screen.getByRole("tab", { name: "Power and focus" }));
    await user.click(await screen.findByRole("button", { name: "Restart into BIOS" }));
    expect(api.biosRestart).not.toHaveBeenCalled();

    await user.click(screen.getByRole("button", { name: "Yes, restart" }));
    expect(api.biosRestart).toHaveBeenCalled();
    expect(await screen.findByRole("button", { name: "Cancel restart" })).toBeTruthy();
  });

  it("repairs a Steam game", async () => {
    const user = userEvent.setup();
    api.gamesVerify.mockResolvedValue({ ok: true, message: "Checking.", restartNeeded: false });
    render(<GamingPage />);

    await user.click(await screen.findByRole("button", { name: "Boosts for Solo Game" }));
    await user.click(screen.getByRole("button", { name: "Repair Solo Game" }));

    expect(api.gamesVerify).toHaveBeenCalledWith("a");
  });

  it("saves the focus app list", async () => {
    const user = userEvent.setup();
    api.focusSave.mockResolvedValue({ ok: true, message: "Focus mode was saved.", restartNeeded: false });
    render(<GamingPage />);

    await user.click(screen.getByRole("tab", { name: "Power and focus" }));
    expect(((await screen.findByLabelText("Discord")) as HTMLInputElement).checked).toBe(true);

    await user.click(screen.getByLabelText("Spotify"));

    await waitFor(() => expect(api.focusSave).toHaveBeenCalledWith(["Discord", "Spotify"]));
  });

  it("explains the anti-cheat rules and keeps the tweaks", async () => {
    const user = userEvent.setup();
    render(<GamingPage />);

    await user.click(screen.getByRole("tab", { name: "Anti-cheat" }));
    expect(screen.getByText(/haven't been tested against each anti-cheat/)).toBeTruthy();
    expect(screen.getAllByText("Skipped")).toHaveLength(2);
    expect(screen.getByText("Locked")).toBeTruthy();

    await user.click(screen.getByRole("tab", { name: "Tweaks" }));
    expect(screen.getByText("Gaming tweaks")).toBeTruthy();
  });
});
