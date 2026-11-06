// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from "vitest";
import { applyTheme, loadTheme, themeChoices } from "./theme";

afterEach(() => {
  localStorage.clear();
  vi.unstubAllGlobals();
});

function windowsPrefersDark(dark: boolean) {
  vi.stubGlobal("matchMedia", () => ({ matches: dark }));
}

describe("themes", () => {
  it("starts on Obsidian", () => {
    expect(loadTheme()).toBe("obsidian");
    expect(themeChoices[0].value).toBe("obsidian");
  });

  it("offers every chosen look and remembers the pick", () => {
    expect(themeChoices.map((choice) => choice.value)).toEqual(["obsidian", "fluent", "hero", "forge", "light", "system"]);

    applyTheme("forge");

    expect(document.documentElement.dataset.theme).toBe("forge");
    expect(loadTheme()).toBe("forge");
  });

  it("falls back to Obsidian for an old or unknown saved value", () => {
    localStorage.setItem("tuneforge.theme", "dark");

    expect(loadTheme()).toBe("obsidian");
  });

  it("follows Windows between Obsidian and Light", () => {
    windowsPrefersDark(true);
    applyTheme("system");
    expect(document.documentElement.dataset.theme).toBe("obsidian");

    windowsPrefersDark(false);
    applyTheme("system");
    expect(document.documentElement.dataset.theme).toBe("light");
  });
});
