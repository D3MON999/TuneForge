import {
  Activity,
  Flame,
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
import { RegistryBackupCard } from "./ExtraPanels";
import { GamingPage } from "./GamingPage";
import { BackupCard, GlobalSearch, guideShouldShow, PageHelp, type SearchTarget, SetupGuide } from "./Guide";
import { HistoryPage } from "./HistoryPage";
import { MonitorPage } from "./MonitorPage";
import { NetworkPage } from "./NetworkPage";
import { RepairPage } from "./RepairPage";
import { AdvancedSwitches, SecurityCard, WindowsTools } from "./SystemTools";
import { applyTheme, loadTheme, themeChoices, type ThemeChoice } from "./theme";
import { TweaksPage } from "./TweaksPage";
import { UpdateCard } from "./UpdateCard";

interface Page {
  id: string;
  label: string;
  icon: LucideIcon;
  subtitle?: string;
}

const pages: Page[] = [
  { id: "dashboard", label: "Dashboard", icon: LayoutDashboard },
  { id: "optimize", label: "Optimize", icon: SlidersHorizontal, subtitle: "Pick the Windows settings you want changed." },
  { id: "gaming", label: "Gaming", icon: Gamepad2, subtitle: "Boost your games, then put everything back when they close." },
  { id: "network", label: "Network", icon: Globe, subtitle: "Test your connection and choose your DNS." },
  { id: "cleanup", label: "Cleanup", icon: Sparkles, subtitle: "Clear out files you don't need." },
  { id: "apps", label: "Apps", icon: Package, subtitle: "Control what's installed and what starts with Windows." },
  { id: "privacy", label: "Privacy", icon: ShieldCheck, subtitle: "Choose what Windows collects and shows you." },
  { id: "repair", label: "Repair", icon: Wrench, subtitle: "Run Windows' own repair tools." },
  { id: "monitor", label: "Monitor", icon: Activity, subtitle: "Check disks, errors and hardware." },
  { id: "history", label: "History", icon: History, subtitle: "See what changed, and undo it." },
  { id: "settings", label: "Settings", icon: Settings, subtitle: "Make TuneForge your own." },
];

/** Pages that list tweaks, and the catalog categories each one shows. */
const tweakPages: Record<string, { categories: string[]; showPresets?: boolean }> = {
  optimize: {
    categories: ["performance", "privacy", "ai", "windows", "taskbar", "start-search", "explorer", "appearance", "update", "browser", "network", "advanced"],
    showPresets: true,
  },
  privacy: { categories: ["privacy", "ai"] },
};

function NavItem({ item, current, onClick }: { item: Page; current: boolean; onClick: () => void }) {
  const Icon = item.icon;
  return (
    <button
      type="button"
      aria-current={current ? "page" : undefined}
      onClick={onClick}
      className={`nav-item relative flex min-h-11 w-full cursor-pointer items-center gap-3 rounded-md px-3 text-left ${
        current ? "bg-muted font-medium" : "text-muted-foreground hover:bg-muted"
      }`}
    >
      <Icon aria-hidden="true" className="size-[18px]" />
      {item.label}
    </button>
  );
}

function ThemePicker({ theme, onChange }: { theme: ThemeChoice; onChange: (choice: ThemeChoice) => void }) {
  return (
    <section aria-labelledby="theme-title" className="rounded-lg border border-border bg-card p-6">
      <h2 id="theme-title" className="text-lg font-semibold">
        Theme
      </h2>
      <div role="group" aria-labelledby="theme-title" className="mt-3 grid grid-cols-2 gap-2 lg:grid-cols-3">
        {themeChoices.map((choice) => (
          <button
            key={choice.value}
            type="button"
            aria-pressed={theme === choice.value}
            onClick={() => onChange(choice.value)}
            className={`flex min-h-11 cursor-pointer items-center gap-3 rounded-md border px-3 text-left font-medium ${
              theme === choice.value ? "border-primary" : "border-border"
            }`}
          >
            <span
              aria-hidden="true"
              className="flex size-6 shrink-0 items-center justify-center rounded-full border border-track"
              style={{ background: choice.swatch }}
            >
              <span className="size-2.5 rounded-full" style={{ background: choice.accent }} />
            </span>
            {choice.label}
          </button>
        ))}
      </div>
    </section>
  );
}

export function App() {
  const [active, setActive] = useState("dashboard");
  const [theme, setTheme] = useState<ThemeChoice>(loadTheme);
  const [target, setTarget] = useState<SearchTarget | null>(null);
  const [guide, setGuide] = useState(guideShouldShow);

  useEffect(() => applyTheme(theme), [theme]);

  function open(next: SearchTarget) {
    setTarget(next);
    setActive(next.page);
  }

  function navigate(id: string) {
    setTarget(null);
    setActive(id);
  }

  const page = pages.find((item) => item.id === active) ?? pages[0];
  const settings = pages[pages.length - 1];

  return (
    <div className="flex h-full">
      <nav aria-label="Main" className="flex w-56 shrink-0 flex-col overflow-y-auto bg-sidebar p-2">
        <p className="flex items-center gap-2.5 px-3 pt-2 pb-4 text-[15px] font-semibold">
          <Flame aria-hidden="true" className="size-5 text-primary" />
          TuneForge
        </p>
        <GlobalSearch onOpen={open} />
        <ul className="flex flex-col gap-0.5">
          {pages.slice(0, -1).map((item) => (
            <li key={item.id}>
              <NavItem item={item} current={item.id === active} onClick={() => navigate(item.id)} />
            </li>
          ))}
        </ul>
        <div className="mt-auto pt-2">
          <NavItem item={settings} current={active === settings.id} onClick={() => navigate(settings.id)} />
        </div>
      </nav>

      <main className="min-w-0 flex-1 overflow-y-auto p-7">
        <div className="mx-auto max-w-5xl">
          {page.id !== "dashboard" && (
            <header className="hero mb-5 px-6 py-5">
              <h1 className="text-[27px] leading-tight font-semibold tracking-tight">{page.label}</h1>
              <p className="mt-0.5 text-muted-foreground">{page.subtitle}</p>
            </header>
          )}
          <PageHelp key={page.id} page={page.id} />
          {page.id === "dashboard" && <DashboardPage onNavigate={navigate} />}
          {page.id === "settings" && (
            <div className="flex flex-col gap-5">
              <ThemePicker theme={theme} onChange={setTheme} />
              <BackupCard onGuide={() => setGuide(true)} />
              <AutomationSettings />
              <UpdateCard />
              <EngineCard />
            </div>
          )}
          {page.id === "privacy" && <SecurityCard />}
          {tweakPages[page.id] && (
            <TweaksPage key={`${page.id}${target?.query}`} {...tweakPages[page.id]} initialQuery={target?.query} />
          )}
          {page.id === "optimize" && <AdvancedSwitches />}
          {page.id === "history" && <HistoryPage />}
          {page.id === "cleanup" && (
            <>
              <CleanupPage />
              <MemoryCard />
            </>
          )}
          {page.id === "apps" && <AppsPage key={target?.tab} initialTab={target?.tab} />}
          {page.id === "repair" && (
            <>
              <RepairPage />
              <RegistryBackupCard />
              <WindowsTools />
            </>
          )}
          {page.id === "gaming" && <GamingPage key={target?.tab} initialTab={target?.tab} initialQuery={target?.query} />}
          {page.id === "network" && <NetworkPage key={target?.tab} initialTab={target?.tab} />}
          {page.id === "monitor" && <MonitorPage key={target?.tab} initialTab={target?.tab} />}
        </div>
      </main>
      {guide && <SetupGuide onDone={() => setGuide(false)} />}
    </div>
  );
}
