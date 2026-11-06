// @vitest-environment happy-dom
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import type { ChangeSet, Tweak } from "./bridge";
import { bridge } from "./bridge";
import { HistoryPage } from "./HistoryPage";
import { TweaksPage } from "./TweaksPage";

vi.mock("./bridge", () => ({
  bridge: { scan: vi.fn(), preview: vi.fn(), apply: vi.fn(), history: vi.fn(), undo: vi.fn() },
}));

const api = vi.mocked(bridge);

function tweak(id: string, title: string, overrides: Partial<Tweak> = {}): Tweak {
  return {
    id,
    title,
    category: "privacy",
    group: null,
    risk: "low",
    impact: null,
    restart: "none",
    presets: [],
    requiresAdmin: false,
    state: "not-applied",
    ...overrides,
  };
}

const catalog = [
  tweak("telemetry", "Disable telemetry", { presets: ["minimal"], risk: "medium", impact: "Some features stop." }),
  tweak("copilot", "Disable Copilot", { category: "ai", presets: ["standard"] }),
  tweak("done", "Already done", { state: "applied", presets: ["minimal"] }),
  tweak("old", "Too old", { state: "unsupported" }),
  tweak("home", "Open to Home", { category: "ai", group: "explorer-opens-to" }),
  tweak("pc", "Open to This PC", { category: "ai", group: "explorer-opens-to" }),
];

beforeAll(() => {
  // happy-dom has no modal dialogs, so open them directly.
  HTMLDialogElement.prototype.showModal = function showModal() {
    this.setAttribute("open", "");
  };
});

beforeEach(() => {
  vi.resetAllMocks();
  api.scan.mockResolvedValue(catalog);
  api.preview.mockImplementation(async (ids) =>
    ids.map((id) => ({
      id,
      title: `Preview ${id}`,
      restart: "reboot" as const,
      impact: null,
      changes: [{ setting: `HKCU\\Test\\${id}`, before: "Not set", after: "1" }],
    })),
  );
});

afterEach(cleanup);

const page = () => render(<TweaksPage categories={["privacy", "ai"]} showPresets />);

describe("TweaksPage", () => {
  it("lists supported tweaks and hides unsupported ones", async () => {
    page();

    expect(await screen.findByText("Disable telemetry")).toBeTruthy();
    expect(screen.getByText("Some features stop.")).toBeTruthy();
    expect(screen.getByText("Medium risk")).toBeTruthy();
    expect(screen.queryByText("Too old")).toBeNull();
  });

  it("can't select something already applied", async () => {
    page();

    const box = (await screen.findByLabelText(/Already done/)) as HTMLInputElement;

    expect(box.disabled).toBe(true);
  });

  it("filters by search", async () => {
    const user = userEvent.setup();
    page();
    await screen.findByText("Disable telemetry");

    await user.type(screen.getByRole("searchbox"), "copilot");

    expect(screen.getByText("Disable Copilot")).toBeTruthy();
    expect(screen.queryByText("Disable telemetry")).toBeNull();
  });

  it("says when nothing matches", async () => {
    const user = userEvent.setup();
    page();
    await screen.findByText("Disable telemetry");

    await user.type(screen.getByRole("searchbox"), "nothing like this");

    expect(screen.getByText("No tweaks match your search.")).toBeTruthy();
  });

  it("applies a preset, leaving out applied tweaks", async () => {
    const user = userEvent.setup();
    page();
    await screen.findByText("Disable telemetry");

    await user.click(screen.getByRole("button", { name: "Standard" }));

    expect(screen.getByText("2 selected")).toBeTruthy();
  });

  it("keeps one choice per group", async () => {
    const user = userEvent.setup();
    page();

    await user.click(await screen.findByLabelText("Open to Home"));
    await user.click(screen.getByLabelText("Open to This PC"));

    expect((screen.getByLabelText("Open to Home") as HTMLInputElement).checked).toBe(false);
    expect(screen.getByText("1 selected")).toBeTruthy();
  });

  it("shows changes first, and applies only after confirming", async () => {
    const user = userEvent.setup();
    api.apply.mockResolvedValue({
      changeSetId: 7,
      restorePoint: "requested",
      results: [{ id: "telemetry", status: "applied", message: null }],
    });
    page();

    await user.click(await screen.findByLabelText(/Disable telemetry/));
    await user.click(screen.getByRole("button", { name: "Review changes" }));

    const dialog = within(await screen.findByRole("dialog", { hidden: true }));
    expect(await dialog.findByText("Preview telemetry")).toBeTruthy();
    expect(dialog.getByText("Some changes need a restart.")).toBeTruthy();
    expect(api.apply).not.toHaveBeenCalled();

    await user.click(dialog.getByRole("button", { name: "Apply changes", hidden: true }));

    expect(await dialog.findByText("1 of 1 applied.")).toBeTruthy();
    expect(api.apply).toHaveBeenCalledWith(["telemetry"], true);

    await user.click(dialog.getByRole("button", { name: "Close", hidden: true }));
    await waitFor(() => expect(api.scan).toHaveBeenCalledTimes(2));
    expect(screen.queryByText("1 selected")).toBeNull();
  });

  it("can skip the restore point", async () => {
    const user = userEvent.setup();
    api.apply.mockResolvedValue({ changeSetId: 8, restorePoint: "skipped", results: [] });
    page();

    await user.click(await screen.findByLabelText(/Disable telemetry/));
    await user.click(screen.getByRole("button", { name: "Review changes" }));
    const dialog = within(await screen.findByRole("dialog", { hidden: true }));
    await user.click(await dialog.findByLabelText("Create a restore point first"));
    await user.click(dialog.getByRole("button", { name: "Apply changes", hidden: true }));

    await waitFor(() => expect(api.apply).toHaveBeenCalledWith(["telemetry"], false));
  });

  it("explains partial failures", async () => {
    const user = userEvent.setup();
    api.apply.mockResolvedValue({
      changeSetId: 9,
      restorePoint: "unavailable",
      results: [{ id: "telemetry", status: "failed", message: "Windows refused this change." }],
    });
    page();

    await user.click(await screen.findByLabelText(/Disable telemetry/));
    await user.click(screen.getByRole("button", { name: "Review changes" }));
    const dialog = within(await screen.findByRole("dialog", { hidden: true }));
    await dialog.findByText("Preview telemetry");
    await user.click(dialog.getByRole("button", { name: "Apply changes", hidden: true }));

    expect(await dialog.findByText("Windows refused this change.")).toBeTruthy();
    expect(dialog.getByText(/System Restore is off/)).toBeTruthy();
  });

  it("shows a declined admin prompt as an error and keeps the selection", async () => {
    const user = userEvent.setup();
    api.apply.mockRejectedValue(new Error("Admin permission was declined."));
    page();

    await user.click(await screen.findByLabelText(/Disable telemetry/));
    await user.click(screen.getByRole("button", { name: "Review changes" }));
    const dialog = within(await screen.findByRole("dialog", { hidden: true }));
    await dialog.findByText("Preview telemetry");
    await user.click(dialog.getByRole("button", { name: "Apply changes", hidden: true }));

    expect(await dialog.findByText("Admin permission was declined.")).toBeTruthy();
    await user.click(dialog.getByRole("button", { name: "Close", hidden: true }));
    expect(screen.getByText("1 selected")).toBeTruthy();
  });

  it("reports a failed scan", async () => {
    api.scan.mockRejectedValue(new Error("Scan failed."));
    page();

    expect(await screen.findByText("Scan failed.")).toBeTruthy();
  });
});

describe("HistoryPage", () => {
  const sets: ChangeSet[] = [
    { Id: 2, CreatedUtc: "2026-11-06T10:00:00.0000000Z", RestorePoint: "requested", UndoneUtc: null, TweakIds: ["a", "b"] },
    { Id: 1, CreatedUtc: "2026-11-05T10:00:00.0000000Z", RestorePoint: "skipped", UndoneUtc: "2026-11-06T09:00:00Z", TweakIds: ["a"] },
  ];

  it("says when there is no history", async () => {
    api.history.mockResolvedValue([]);
    render(<HistoryPage />);

    expect(await screen.findByText("No changes have been made yet.")).toBeTruthy();
  });

  it("offers undo only for active changes", async () => {
    api.history.mockResolvedValue(sets);
    render(<HistoryPage />);

    expect(await screen.findByText("2 tweaks")).toBeTruthy();
    expect(screen.getByText("1 tweak, undone")).toBeTruthy();
    expect(screen.getAllByRole("button", { name: "Undo" })).toHaveLength(1);
  });

  it("undoes a change and shows problems", async () => {
    const user = userEvent.setup();
    api.history.mockResolvedValue(sets);
    api.undo.mockResolvedValue({ changeSetId: 2, restored: 3, problems: ["One setting was left alone."] });
    render(<HistoryPage />);

    await user.click(await screen.findByRole("button", { name: "Undo" }));

    expect(await screen.findByText("Restored 3 settings.")).toBeTruthy();
    expect(screen.getByText("One setting was left alone.")).toBeTruthy();
    expect(api.undo).toHaveBeenCalledWith(2);
  });

  it("shows a declined admin prompt", async () => {
    const user = userEvent.setup();
    api.history.mockResolvedValue(sets);
    api.undo.mockRejectedValue(new Error("Admin permission was declined."));
    render(<HistoryPage />);

    await user.click(await screen.findByRole("button", { name: "Undo" }));

    expect(await screen.findByRole("alert")).toBeTruthy();
  });
});
