// @vitest-environment happy-dom
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { bridge, type Tweak } from "./bridge";
import { ReviewDialog } from "./ReviewDialog";
import { AdvancedSwitches } from "./SystemTools";
import { TweaksPage } from "./TweaksPage";

vi.mock("./bridge", () => ({
  bridge: { scan: vi.fn(), preview: vi.fn(), apply: vi.fn(), advancedList: vi.fn(), advancedSet: vi.fn() },
}));

const api = vi.mocked(bridge);

const hazard: Tweak = {
  id: "advanced.wpbt", title: "Block firmware software", category: "advanced", group: null, risk: "high", impact: "Tools may stop.",
  restart: "reboot", presets: [], requiresAdmin: true, state: "not-applied", description: "What it does.", hazard: true,
};

const hazardPreview = { id: "advanced.wpbt", title: "Block firmware software", restart: "reboot" as const, impact: null, changes: [], hazard: true };

const switchItem = {
  id: "hpet", title: "Prefer the platform tick", description: "Changes a timer.", warning: "Results vary.", risk: "high" as const,
  restartNeeded: true, reversible: true, enabled: false, hazard: true,
};

beforeAll(() => {
  HTMLDialogElement.prototype.showModal = function showModal() {
    this.setAttribute("open", "");
  };
});

beforeEach(() => {
  vi.resetAllMocks();
  api.scan.mockResolvedValue([hazard]);
  api.preview.mockResolvedValue([hazardPreview]);
  api.apply.mockResolvedValue({ changeSetId: 1, restorePoint: "skipped", results: [] });
  api.advancedSet.mockResolvedValue({ ok: true, message: "Done.", restartNeeded: true });
});

afterEach(cleanup);

describe("Riot hazard warnings", () => {
  it("labels the tweak and keeps it off until confirmed", async () => {
    const user = userEvent.setup();
    render(<TweaksPage categories={["advanced"]} />);

    expect((await screen.findByText("Extremely hazardous for Riot Vanguard")).textContent).toBeTruthy();
    const toggle = screen.getByRole("checkbox", { name: /Block firmware software/ }) as HTMLInputElement;
    expect(toggle.checked).toBe(false);

    await user.click(toggle);
    expect(toggle.checked).toBe(false);
    expect((screen.getByRole("button", { name: "Turn on anyway", hidden: true }) as HTMLButtonElement).disabled).toBe(true);

    await user.click(screen.getByRole("button", { name: "Keep it off", hidden: true }));
    expect(toggle.checked).toBe(false);
  });

  it("selects it only after the tick and the second click", async () => {
    const user = userEvent.setup();
    render(<TweaksPage categories={["advanced"]} />);

    await user.click(await screen.findByRole("checkbox", { name: /Block firmware software/ }));
    await user.click(screen.getByRole("checkbox", { name: /I understand the risk/, hidden: true }));
    await user.click(screen.getByRole("button", { name: "Turn on anyway", hidden: true }));

    expect((screen.getByRole("checkbox", { name: /Block firmware software/ }) as HTMLInputElement).checked).toBe(true);
    expect(api.apply).not.toHaveBeenCalled();
  });

  it("blocks Apply in review until confirmed, however the tweak got selected", async () => {
    const user = userEvent.setup();
    render(<ReviewDialog ids={["advanced.wpbt"]} onClose={vi.fn()} />);

    const apply = (await screen.findByRole("button", { name: "Apply changes", hidden: true })) as HTMLButtonElement;
    expect(apply.disabled).toBe(true);

    await user.click(screen.getByRole("checkbox", { name: /I understand the risk/, hidden: true }));
    await user.click(apply);

    expect(api.apply).toHaveBeenCalledWith(["advanced.wpbt"], true, true);
  });

  it("confirms a system switch before running it", async () => {
    const user = userEvent.setup();
    api.advancedList.mockResolvedValue([switchItem]);
    render(<AdvancedSwitches />);

    await user.click(await screen.findByRole("button", { name: "Turn on Prefer the platform tick" }));
    expect(api.advancedSet).not.toHaveBeenCalled();

    await user.click(screen.getByRole("checkbox", { name: /I understand the risk/, hidden: true }));
    await user.click(screen.getByRole("button", { name: "Turn on anyway", hidden: true }));

    expect(api.advancedSet).toHaveBeenCalledWith("hpet", true, true);
  });

  it("turns a hazardous switch off without the warning", async () => {
    const user = userEvent.setup();
    api.advancedList.mockResolvedValue([{ ...switchItem, enabled: true }]);
    render(<AdvancedSwitches />);

    await user.click(await screen.findByRole("button", { name: "Turn off Prefer the platform tick" }));
    expect(screen.queryByText("Turn on anyway")).toBeNull();

    await user.click(screen.getByRole("button", { name: "Yes, continue" }));
    expect(api.advancedSet).toHaveBeenCalledWith("hpet", false, false);
  });
});
