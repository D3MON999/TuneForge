import {
  Activity,
  Gamepad2,
  Globe,
  History,
  LayoutDashboard,
  type LucideIcon,
  Package,
  Settings,
  ShieldCheck,
  SlidersHorizontal,
  Sparkles,
  Wrench,
} from "lucide-react";
import { useEffect, useState } from "react";
import { AppsPage } from "./AppsPage";
import { AutomationSettings, MemoryCard } from "./AutomationPanels";
import { CleanupPage } from "./CleanupPage";
import { DashboardPage } from "./DashboardPage";
import { EngineCard } from "./EngineCard";
import { GamingPage } from "./GamingPage";
import { HistoryPage } from "./HistoryPage";
import { MonitorPage } from "./MonitorPage";
import { NetworkPage } from "./NetworkPage";
import { RepairPage } from "./RepairPage";
import { applyTheme, loadTheme, type ThemeChoice } from "./theme";
import { TweaksPage } from "./TweaksPage";
import { UpdateCard } from "./UpdateCard";

interface Page {
  id: string;
  label: string;
  icon: LucideIcon;
}

const pages: Page[] = [
  { id: "dashboard", label: "Dashboard", icon: LayoutDashboard },
  { id: "optimize", label: "Optimize", icon: SlidersHorizontal },
  { id: "gaming", label: "Gaming", icon: Gamepad2 },
  { id: "network", label: "Network", icon: Globe },
  { id: "cleanup", label: "Cleanup", icon: Sparkles },
  { id: "apps", label: "Apps", icon: Package },
  { id: "privacy", label: "Privacy", icon: ShieldCheck },
  { id: "repair", label: "Repair", icon: Wrench },
  { id: "monitor", label: "Monitor", icon: Activity },
  { id: "history", label: "History", icon: History },
  { id: "settings", label: "Settings", icon: Settings },
];

/** Pages that list tweaks, and the catalog categories each one shows. */
const tweakPages: Record<string, { categories: string[]; showPresets?: boolean }> = {
  optimize: {
    categories: ["privacy", "ai", "windows", "taskbar", "start-search", "explorer", "appearance", "update", "browser"],
    showPresets: true,
  },
  privacy: { categories: ["privacy", "ai"] },
};

const themes: { value: ThemeChoice; label: string }[] = [
  { value: "system", label: "Match Windows" },
  { value: "light", label: "Light" },
  { value: "dark", label: "Dark" },
];

export function App() {
  const [active, setActive] = useState("dashboard");
  const [theme, setTheme] = useState<ThemeChoice>(loadTheme);

  useEffect(() => applyTheme(theme), [theme]);

  const page = pages.find((item) => item.id === active) ?? pages[0];

  return (
    <div className="flex h-full">
      <nav aria-label="Main" className="flex w-60 shrink-0 flex-col border-r border-border bg-card p-3">
        <p className="px-3 py-4 text-lg font-bold tracking-tight">TuneForge</p>
        <ul className="flex flex-col gap-1">
          {pages.map((item) => {
            const Icon = item.icon;
            const current = item.id === active;
            return (
              <li key={item.id}>
                <button
                  type="button"
                  aria-current={current ? "page" : undefined}
                  onClick={() => setActive(item.id)}
                  className={`flex min-h-11 w-full cursor-pointer items-center gap-3 rounded-md px-3 text-left transition-colors duration-200 ${
                    current ? "bg-muted font-medium" : "text-muted-foreground hover:bg-muted"
                  }`}
                >
                  <Icon aria-hidden="true" className="size-5" />
                  {item.label}
                </button>
              </li>
            );
          })}
        </ul>
      </nav>

      <main className="flex-1 overflow-y-auto p-8">
        <h1 className="text-2xl font-semibold tracking-tight">{page.label}</h1>
        <div className="mt-6 max-w-3xl">
          {page.id === "dashboard" && <DashboardPage />}
          {page.id === "settings" && (
            <div className="flex flex-col gap-6">
            <div className="rounded-lg border border-border bg-card p-6">
              <label htmlFor="theme" className="block font-medium">
                Theme
              </label>
              <select
                id="theme"
                value={theme}
                onChange={(event) => setTheme(event.target.value as ThemeChoice)}
                className="mt-2 min-h-11 cursor-pointer rounded-md border border-border bg-background px-3"
              >
                {themes.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </select>
            </div>
            <AutomationSettings />
            <UpdateCard />
            <EngineCard />
            </div>
          )}
          {tweakPages[page.id] && <TweaksPage key={page.id} {...tweakPages[page.id]} />}
          {page.id === "history" && <HistoryPage />}
          {page.id === "cleanup" && (
            <>
              <CleanupPage />
              <MemoryCard />
            </>
          )}
          {page.id === "apps" && <AppsPage />}
          {page.id === "repair" && <RepairPage />}
          {page.id === "gaming" && <GamingPage />}
          {page.id === "network" && <NetworkPage />}
          {page.id === "monitor" && <MonitorPage />}
        </div>
      </main>
    </div>
  );
}
