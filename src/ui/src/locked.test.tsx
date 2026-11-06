// @vitest-environment happy-dom
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { bridge, type Tweak } from "./bridge";
import { TweaksPage } from "./TweaksPage";

vi.mock("./bridge", () => ({ bridge: { scan: vi.fn(), removeLock: vi.fn() } }));

const api = vi.mocked(bridge);

const tweak: Tweak = {
  id: "privacy.clipboard-history", title: "Disable clipboard history", category: "privacy", group: null, risk: "low", impact: null,
  restart: "signout", presets: [], requiresAdmin: true, state: "applied", description: "Turns off the Win+V list.", locked: true,
};

const normal: Tweak = { ...tweak, id: "privacy.other", title: "Disable something else", state: "not-applied", locked: false };

const group = () => screen.findByRole("region", { name: /Locked by another app/ });

beforeEach(() => {
  vi.resetAllMocks();
  api.scan.mockResolvedValue([tweak, normal]);
});

afterEach(cleanup);

describe("Locked group", () => {
  it("lists locked settings apart from the normal ones", async () => {
    render(<TweaksPage categories={["privacy"]} />);

    expect(within(await group()).getByText("Disable clipboard history")).toBeTruthy();
    expect(within(await group()).queryByText("Disable something else")).toBeNull();
    expect(screen.getByText("Locked by another app (1)")).toBeTruthy();
    expect(screen.queryByRole("checkbox", { name: /Disable clipboard history/ })).toBeNull();
    expect(screen.getByRole("checkbox", { name: /Disable something else/ })).toBeTruthy();
  });

  it("stays hidden when nothing is locked", async () => {
    api.scan.mockResolvedValue([normal]);
    render(<TweaksPage categories={["privacy"]} />);

    await screen.findByText("Disable something else");
    expect(screen.queryByText(/Locked by another app/)).toBeNull();
  });

  it("asks first, then removes the lock and refreshes the page", async () => {
    const user = userEvent.setup();
    api.removeLock.mockResolvedValue({ ok: true, message: "Saved. You can undo this from History.", restartNeeded: false });
    render(<TweaksPage categories={["privacy"]} />);

    await user.click(await screen.findByRole("button", { name: "Remove lock on Disable clipboard history" }));
    expect(api.removeLock).not.toHaveBeenCalled();

    api.scan.mockResolvedValue([{ ...tweak, state: "not-applied", locked: false }, normal]);
    await user.click(screen.getByRole("button", { name: "Yes, remove it" }));

    expect(api.removeLock).toHaveBeenCalledWith("privacy.clipboard-history");
    expect(await screen.findByText("Saved. You can undo this from History.")).toBeTruthy();
    await waitFor(() => expect(screen.getByRole("checkbox", { name: /Disable clipboard history/ })).toBeTruthy());
    expect(screen.queryByText(/Locked by another app \(/)).toBeNull();
  });

  it("does nothing on Cancel", async () => {
    const user = userEvent.setup();
    render(<TweaksPage categories={["privacy"]} />);

    await user.click(await screen.findByRole("button", { name: "Remove lock on Disable clipboard history" }));
    await user.click(screen.getByRole("button", { name: "Cancel" }));

    expect(api.removeLock).not.toHaveBeenCalled();
    expect(screen.queryByRole("button", { name: "Yes, remove it" })).toBeNull();
  });

  it("keeps the setting listed and says why if it fails", async () => {
    const user = userEvent.setup();
    api.removeLock.mockRejectedValue(new Error("The request was declined."));
    render(<TweaksPage categories={["privacy"]} />);

    await user.click(await screen.findByRole("button", { name: "Remove lock on Disable clipboard history" }));
    await user.click(screen.getByRole("button", { name: "Yes, remove it" }));

    expect(await screen.findByText("The request was declined.")).toBeTruthy();
    expect(screen.getByText("Locked by another app (1)")).toBeTruthy();
  });

  it("follows the search box", async () => {
    const user = userEvent.setup();
    render(<TweaksPage categories={["privacy"]} />);
    await group();

    await user.type(screen.getByRole("searchbox", { name: "Search tweaks" }), "something");

    expect(screen.queryByText(/Locked by another app/)).toBeNull();
  });
});
