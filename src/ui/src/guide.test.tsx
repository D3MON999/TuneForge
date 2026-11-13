// @vitest-environment happy-dom
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { bridge, type Tweak } from "./bridge";
import { BackupCard, GlobalSearch, guideShouldShow, PageHelp, SetupGuide } from "./Guide";
import { pageHelp } from "./help";
import { profileIds } from "./profiles";

vi.mock("./bridge", () => ({
  bridge: { scan: vi.fn(), preview: vi.fn(), apply: vi.fn(), settingsExport: vi.fn(), settingsImport: vi.fn() },
}));

const api = vi.mocked(bridge);

function tweak(id: string, overrides: Partial<Tweak> = {}): Tweak {
  return { id, title: id, category: "privacy", group: null, risk: "low", impact: null, restart: "none", presets: [], requiresAdmin: false, state: "not-applied", description: "", ...overrides };
}

const tweaks = [
  tweak("gaming.game-mode", { category: "gaming", title: "Game Mode" }),
  tweak("windows.print-spooler", { title: "Disable printing" }),
  tweak("windows.fax", { title: "Disable fax" }),
  tweak("privacy.disable-telemetry", { presets: ["minimal"], title: "Disable telemetry", description: "Limits usage data." }),
  tweak("windows.time-as-utc", { state: "applied" }),
];

beforeAll(() => {
  HTMLDialogElement.prototype.showModal = function showModal() {
    this.setAttribute("open", "");
  };
});

beforeEach(() => {
  vi.resetAllMocks();
  localStorage.clear();
  api.scan.mockResolvedValue(tweaks);
  api.preview.mockResolvedValue([]);
});

afterEach(cleanup);

describe("profiles", () => {
  it("picks only available tweaks that aren't already on", () => {
    expect(profileIds(["noPrinter"], tweaks)).toEqual(["windows.print-spooler", "windows.fax"]);
    expect(profileIds(["dualBoot"], tweaks)).toEqual([]);
  });
});

describe("PageHelp", () => {
  it("covers every page and opens on request", async () => {
    const user = userEvent.setup();
    expect(Object.keys(pageHelp).sort()).toEqual(
      ["apps", "cleanup", "dashboard", "gaming", "history", "monitor", "network", "optimize", "privacy", "repair", "settings", "testers"],
    );
    render(<PageHelp page="repair" />);

    expect(screen.queryByText(/Long repairs/)).toBeNull();
    await user.click(screen.getByRole("button", { name: "How this works" }));

    expect(screen.getByText(/Long repairs/)).toBeTruthy();
  });
});

describe("GlobalSearch", () => {
  it("finds pages and tweaks, and opens the result", async () => {
    const user = userEvent.setup();
    const onOpen = vi.fn();
    render(<GlobalSearch onOpen={onOpen} />);
    const box = screen.getByLabelText("Search everything");

    await user.click(box);
    await waitFor(() => expect(api.scan).toHaveBeenCalled());
    await user.type(box, "usage data");
    await user.click(await screen.findByRole("button", { name: /Disable telemetry/ }));
    expect(onOpen).toHaveBeenCalledWith({ page: "optimize", tab: undefined, query: "Disable telemetry" });

    await user.type(box, "chkdsk");
    await user.click(await screen.findByRole("button", { name: /Repair/ }));
    expect(onOpen).toHaveBeenLastCalledWith({ page: "repair", tab: undefined });
  });

  it("sends gaming tweaks to the Gaming page", async () => {
    const user = userEvent.setup();
    const onOpen = vi.fn();
    render(<GlobalSearch onOpen={onOpen} />);
    const box = screen.getByLabelText("Search everything");
    await user.click(box);

    await user.type(box, "game mode");
    await user.click(await screen.findByRole("button", { name: /Game Mode/ }));

    expect(onOpen).toHaveBeenCalledWith({ page: "gaming", tab: "tweaks", query: "Game Mode" });
  });
});

describe("SetupGuide", () => {
  it("shows once, and answers become suggestions for review", async () => {
    const user = userEvent.setup();
    expect(guideShouldShow()).toBe(true);
    render(<SetupGuide onDone={vi.fn()} />);

    await user.click(screen.getByRole("button", { name: "Next", hidden: true }));
    await user.click(screen.getByRole("button", { name: "Do you use a printer? No", hidden: true }));
    await user.click(await screen.findByRole("button", { name: "Review 3 suggestions", hidden: true }));

    expect(api.preview).toHaveBeenCalledWith(["privacy.disable-telemetry", "windows.print-spooler", "windows.fax"]);
  });

  it("can be skipped without changing anything", async () => {
    const user = userEvent.setup();
    const onDone = vi.fn();
    render(<SetupGuide onDone={onDone} />);

    await user.click(screen.getByRole("button", { name: "Skip for now", hidden: true }));

    expect(onDone).toHaveBeenCalled();
    expect(api.apply).not.toHaveBeenCalled();
    expect(guideShouldShow()).toBe(false);
  });
});

describe("BackupCard", () => {
  it("sends imported tweaks to review and reports skipped items", async () => {
    const user = userEvent.setup();
    api.settingsImport.mockResolvedValue({ tweaks: ["windows.fax"], apps: [], ignored: 2 });
    render(<BackupCard onGuide={vi.fn()} />);

    await user.click(screen.getByRole("button", { name: "Import settings" }));

    expect(await screen.findByText(/2 unknown items were skipped/)).toBeTruthy();
    expect(api.preview).toHaveBeenCalledWith(["windows.fax"]);
  });

  it("stays quiet if the file picker is cancelled", async () => {
    const user = userEvent.setup();
    api.settingsImport.mockResolvedValue(null);
    render(<BackupCard onGuide={vi.fn()} />);

    await user.click(screen.getByRole("button", { name: "Import settings" }));

    expect(screen.queryByText(/Loaded/)).toBeNull();
  });
});
