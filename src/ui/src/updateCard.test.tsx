// @vitest-environment happy-dom
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { bridge } from "./bridge";
import { UpdateCard } from "./UpdateCard";

vi.mock("./bridge", () => ({ bridge: { updateCheck: vi.fn(), updateInstall: vi.fn() } }));

const api = vi.mocked(bridge);

beforeEach(() => vi.resetAllMocks());
afterEach(cleanup);

describe("UpdateCard", () => {
  it("does nothing until asked", () => {
    render(<UpdateCard />);

    expect(api.updateCheck).not.toHaveBeenCalled();
  });

  it("offers an update and installs only on a second click", async () => {
    const user = userEvent.setup();
    api.updateCheck.mockResolvedValue({ current: "0.1.0", latest: "0.2.0", available: true, message: "Version 0.2.0 is ready to install." });
    api.updateInstall.mockResolvedValue({ ok: false, message: "The update was cancelled.", restartNeeded: false });
    render(<UpdateCard />);

    await user.click(screen.getByRole("button", { name: "Check for updates" }));
    expect(await screen.findByText("Version 0.2.0 is ready to install.")).toBeTruthy();
    expect(api.updateInstall).not.toHaveBeenCalled();

    await user.click(screen.getByRole("button", { name: "Install version 0.2.0" }));
    expect(await screen.findByText("The update was cancelled.")).toBeTruthy();
  });

  it("doesn't offer an install when up to date", async () => {
    const user = userEvent.setup();
    api.updateCheck.mockResolvedValue({ current: "0.1.0", latest: "0.1.0", available: false, message: "TuneForge is up to date." });
    render(<UpdateCard />);

    await user.click(screen.getByRole("button", { name: "Check for updates" }));

    expect(await screen.findByText("TuneForge is up to date.")).toBeTruthy();
    expect(screen.queryByRole("button", { name: /Install version/ })).toBeNull();
  });
});
