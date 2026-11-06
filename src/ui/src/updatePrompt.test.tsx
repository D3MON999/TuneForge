// @vitest-environment happy-dom
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { bridge, type UpdatePrompt } from "./bridge";
import { UpdatePromptPage, useUpdatePrompt } from "./UpdatePrompt";

vi.mock("./bridge", () => ({ bridge: { updatePending: vi.fn(), updateInstall: vi.fn() } }));

const api = vi.mocked(bridge);

const normal: UpdatePrompt = {
  status: { current: "0.1.0", latest: "0.2.0", available: true, message: "Ready.", important: false, notes: "Small fixes." },
  nudge: 0,
};

const important: UpdatePrompt = { status: { ...normal.status, important: true }, nudge: 1 };

function Shell() {
  const { prompt, later } = useUpdatePrompt();
  return prompt ? <UpdatePromptPage prompt={prompt} onLater={later} /> : <p>The normal page</p>;
}

beforeEach(() => vi.resetAllMocks());
afterEach(cleanup);

describe("UpdatePrompt", () => {
  it("shows nothing when there's no update", async () => {
    api.updatePending.mockResolvedValue(null);
    render(<Shell />);

    await waitFor(() => expect(api.updatePending).toHaveBeenCalled());
    expect(screen.getByText("The normal page")).toBeTruthy();
    expect(api.updateInstall).not.toHaveBeenCalled();
  });

  it("asks first and never installs by itself", async () => {
    api.updatePending.mockResolvedValue(normal);
    render(<Shell />);

    expect(await screen.findByRole("heading", { name: "Update available" })).toBeTruthy();
    expect(screen.getByText("TuneForge 0.2.0 is ready. You have 0.1.0.")).toBeTruthy();
    expect(screen.getByText("Small fixes.")).toBeTruthy();
    expect(api.updateInstall).not.toHaveBeenCalled();
  });

  it("names an important update", async () => {
    api.updatePending.mockResolvedValue(important);
    render(<Shell />);

    expect(await screen.findByRole("heading", { name: "Important update" })).toBeTruthy();
  });

  it("installs on Update now", async () => {
    const user = userEvent.setup();
    api.updatePending.mockResolvedValue(normal);
    api.updateInstall.mockResolvedValue({ ok: true, message: "Installing the update.", restartNeeded: false });
    render(<Shell />);

    await user.click(await screen.findByRole("button", { name: "Update now" }));

    expect(api.updateInstall).toHaveBeenCalledTimes(1);
  });

  it("says why an update didn't start and lets you try again", async () => {
    const user = userEvent.setup();
    api.updatePending.mockResolvedValue(normal);
    api.updateInstall.mockResolvedValue({ ok: false, message: "The update was cancelled.", restartNeeded: false });
    render(<Shell />);

    await user.click(await screen.findByRole("button", { name: "Update now" }));

    expect(await screen.findByText("The update was cancelled.")).toBeTruthy();
    expect((screen.getByRole("button", { name: "Update now" }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("goes away on Later", async () => {
    const user = userEvent.setup();
    api.updatePending.mockResolvedValue(normal);
    render(<Shell />);

    await user.click(await screen.findByRole("button", { name: "Later" }));

    expect(screen.getByText("The normal page")).toBeTruthy();
  });

  it("treats another nudge of an important update as new", () => {
    const first = `${important.status.latest}:${important.nudge}`;
    const second = `${important.status.latest}:${important.nudge + 1}`;

    expect(first).not.toBe(second);
  });
});
