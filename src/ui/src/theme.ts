export const themeChoices = [
  { value: "obsidian", label: "Obsidian", swatch: "#000000", accent: "#ffffff" },
  { value: "fluent", label: "Fluent", swatch: "#202020", accent: "#5eead4" },
  { value: "hero", label: "Hero", swatch: "#1a1b26", accent: "#a78bfa" },
  { value: "forge", label: "Forge", swatch: "#141211", accent: "#fb923c" },
  { value: "light", label: "Light", swatch: "#f3f4f6", accent: "#111111" },
  { value: "system", label: "Match Windows", swatch: "#808080", accent: "#ffffff" },
] as const;

export type ThemeChoice = (typeof themeChoices)[number]["value"];

const storageKey = "tuneforge.theme";

export function loadTheme(): ThemeChoice {
  try {
    const value = localStorage.getItem(storageKey);
    return themeChoices.find((choice) => choice.value === value)?.value ?? "obsidian";
  } catch {
    return "obsidian";
  }
}

export function applyTheme(choice: ThemeChoice): void {
  const followsWindows = choice === "system";
  const dark = followsWindows && matchMedia("(prefers-color-scheme: dark)").matches;
  document.documentElement.dataset.theme = followsWindows ? (dark ? "obsidian" : "light") : choice;

  try {
    localStorage.setItem(storageKey, choice);
  } catch {
    // The choice still applies for this session.
  }
}
