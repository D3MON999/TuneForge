import type { RestartNeed, Tweak } from "./bridge";

export const presetOrder = ["minimal", "standard", "advanced"] as const;
export type Preset = (typeof presetOrder)[number];

/** Turns a tweak on or off. Choosing one option in a group clears the others. */
export function toggle(selected: ReadonlySet<string>, tweak: Tweak, all: readonly Tweak[]): Set<string> {
  const next = new Set(selected);
  if (next.delete(tweak.id)) {
    return next;
  }

  if (tweak.group) {
    for (const other of all) {
      if (other.group === tweak.group) {
        next.delete(other.id);
      }
    }
  }

  next.add(tweak.id);
  return next;
}

/** Presets build on each other, and already-applied tweaks are left out. */
export function presetSelection(preset: Preset, all: readonly Tweak[]): Set<string> {
  const included = presetOrder.slice(0, presetOrder.indexOf(preset) + 1) as readonly string[];
  return new Set(
    all
      .filter((tweak) => tweak.state === "not-applied" && tweak.presets.some((name) => included.includes(name)))
      .map((tweak) => tweak.id),
  );
}

export function matchesSearch(tweak: Tweak, query: string): boolean {
  const words = query.toLowerCase().split(/\s+/).filter(Boolean);
  const text = `${tweak.title} ${tweak.impact ?? ""} ${tweak.category}`.toLowerCase();
  return words.every((word) => text.includes(word));
}

const restartOrder: RestartNeed[] = ["none", "explorer", "signout", "reboot"];

export function strongestRestart(needs: readonly RestartNeed[]): RestartNeed {
  return needs.reduce<RestartNeed>(
    (strongest, need) => (restartOrder.indexOf(need) > restartOrder.indexOf(strongest) ? need : strongest),
    "none",
  );
}

export const restartText: Record<RestartNeed, string> = {
  none: "These changes take effect right away.",
  explorer: "These should appear right away. If one doesn't, sign out and back in.",
  signout: "Some changes need you to sign out and back in.",
  reboot: "Some changes need a restart.",
};
