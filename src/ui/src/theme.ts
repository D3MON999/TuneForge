export type ThemeChoice = "system" | "light" | "dark";

const storageKey = "tuneforge.theme";

export function loadTheme(): ThemeChoice {
  try {
    const value = localStorage.getItem(storageKey);
    return value === "light" || value === "dark" ? value : "system";
  } catch {
    return "system";
  }
}

export function applyTheme(choice: ThemeChoice): void {
  const dark =
    choice === "dark" ||
    (choice === "system" && matchMedia("(prefers-color-scheme: dark)").matches);
  document.documentElement.dataset.theme = dark ? "dark" : "light";

  try {
    localStorage.setItem(storageKey, choice);
  } catch {
    // The choice still applies for this session.
  }
}
