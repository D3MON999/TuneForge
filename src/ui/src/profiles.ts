import type { Tweak } from "./bridge";

/** Ends of tweak IDs, so a list keeps working if a tweak moves to another category. */
const lists = {
  gaming: ["disable-dvr", "game-mode", "disable-mouse-acceleration", "disable-sticky-keys", "multimedia-priority", "foreground-priority", "gpu-scheduling", "throttling-index", "disable-animations", "disable-power-throttling"],
  work: ["disable-suggestions", "disable-lockscreen-tips", "disable-bing", "show-known-file-ext", "enable-end-task", "disable-settings365-ads", "consumer-features", "autocomplete", "copy-file-path"],
  laptop: ["disable-bgapps", "disable-transparency", "disable-animations", "battery-percentage", "delivery-optimization"],
  privacy: ["disable-telemetry", "activity", "tailored-experiences", "typing-data", "input-personalization", "handwriting-sharing", "feedback-requests", "app-launch-tracking", "search-history", "error-reporting"],
  ai: ["disable-copilot", "disable-recall", "disable-click-to-do", "disable-aisvc-auto-start", "disable-edge-ai", "disable-chrome-ai", "disable-paint-ai", "disable-notepad-ai"],
  noPrinter: ["print-spooler", "fax"],
  desktop: ["disable-power-throttling"],
  dualBoot: ["time-as-utc", "disable-fast-startup"],
} as const;

export type ProfileName = keyof typeof lists;

export function profileIds(names: readonly ProfileName[], tweaks: readonly Tweak[]): string[] {
  const ends = names.flatMap((name) => lists[name] as readonly string[]);
  return tweaks
    .filter((tweak) => tweak.state === "not-applied" && ends.some((end) => tweak.id === end || tweak.id.endsWith(`.${end}`)))
    .map((tweak) => tweak.id);
}

export const profileChoices: { id: ProfileName; label: string }[] = [
  { id: "gaming", label: "Gaming" },
  { id: "work", label: "Work" },
  { id: "laptop", label: "Laptop" },
];
