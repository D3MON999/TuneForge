import { describe, expect, it } from "vitest";
import type { Tweak } from "./bridge";
import { matchesSearch, presetSelection, strongestRestart, toggle } from "./selection";

function tweak(id: string, overrides: Partial<Tweak> = {}): Tweak {
  return {
    id,
    title: id,
    category: "privacy",
    group: null,
    risk: "low",
    impact: null,
    restart: "none",
    presets: [],
    requiresAdmin: false,
    state: "not-applied",
    description: "",
    ...overrides,
  };
}

describe("toggle", () => {
  it("adds and removes a tweak", () => {
    const item = tweak("a");
    const on = toggle(new Set(), item, [item]);

    expect(on.has("a")).toBe(true);
    expect(toggle(on, item, [item]).has("a")).toBe(false);
  });

  it("allows one choice per group and keeps other selections", () => {
    const first = tweak("first", { group: "g" });
    const second = tweak("second", { group: "g" });
    const other = tweak("other");
    const all = [first, second, other];

    const result = toggle(new Set(["first", "other"]), second, all);

    expect([...result].sort()).toEqual(["other", "second"]);
  });
});

describe("presetSelection", () => {
  const all = [
    tweak("small", { presets: ["minimal"] }),
    tweak("middle", { presets: ["standard"] }),
    tweak("large", { presets: ["advanced"] }),
    tweak("done", { presets: ["minimal"], state: "applied" }),
    tweak("old", { presets: ["minimal"], state: "unsupported" }),
  ];

  it("builds on smaller presets", () => {
    expect([...presetSelection("standard", all)].sort()).toEqual(["middle", "small"]);
    expect([...presetSelection("advanced", all)].sort()).toEqual(["large", "middle", "small"]);
  });

  it("skips applied and unsupported tweaks", () => {
    expect([...presetSelection("minimal", all)]).toEqual(["small"]);
  });
});

describe("matchesSearch", () => {
  const item = tweak("a", { title: "Disable Bing search", impact: "Web results go away." });

  it("matches every word in any order", () => {
    expect(matchesSearch(item, "search BING")).toBe(true);
    expect(matchesSearch(item, "web")).toBe(true);
    expect(matchesSearch(item, "bing cortana")).toBe(false);
    expect(matchesSearch(item, "   ")).toBe(true);
  });
});

describe("strongestRestart", () => {
  it("picks the biggest restart needed", () => {
    expect(strongestRestart([])).toBe("none");
    expect(strongestRestart(["explorer", "reboot", "signout"])).toBe("reboot");
  });
});
