/** Plain steps shown under "How this works" on every page. */
export const pageHelp: Record<string, string[]> = {
  dashboard: [
    "The score starts at 100 and drops for each problem found. Health checks show the reason for every lost point.",
    "Ignore a problem you already know about and it stops lowering your score. It moves to the Ignored list with its real result, and the top of the page still shows what the score would be with everything counted.",
    "Live numbers update every two seconds while this page is open.",
    "Save report writes a text file you can keep or send to someone helping you. It stays on this PC unless you send it.",
  ],
  optimize: [
    "Turn on the changes you want. Nothing happens yet, so feel free to explore.",
    "Presets and profiles pick a set of changes for you. Minimal is the safest start.",
    "Click Review changes to see exactly what will change, then Apply. Windows asks for permission once.",
    "Every change here can be undone from History. System switches at the bottom have their own off button.",
  ],
  gaming: [
    "Games shows what TuneForge found. Open Boosts on a game to pick what happens for it.",
    "Power, focus and priority boosts run while the game is open and go back when it closes. Keep TuneForge open or in the tray for these.",
    "Anti-cheat explains which boosts are skipped for protected games.",
  ],
  network: [
    "Connection tests measure latency, jitter and packet loss. Lower is better for all three.",
    "A route test shows each stop between you and a server, which helps find where a slowdown starts.",
    "The DNS speed test times real lookups from your connection. Pick the fastest with Use, and restore your earlier DNS at any time.",
  ],
  cleanup: [
    "Sizes are measured first, and only files older than a day are removed from temporary folders.",
    "The safe categories are already on. Read the note on any other category before you turn it on.",
    "Files are deleted for good, so TuneForge asks you to confirm first.",
  ],
  apps: [
    "Startup and Services control what runs in the background. Startup changes can be undone from History.",
    "Built-in apps removes apps that came with Windows, for every account. Only some can be reinstalled.",
    "Install apps and Installed apps use winget, which comes with Windows.",
    "Processes shows what's running now. Ending a process loses any unsaved work.",
  ],
  privacy: [
    "Security status shows your protection. TuneForge can only turn protection on, never off.",
    "The list below works like Optimize: turn on what you want, then review.",
  ],
  repair: [
    "Start with Check system files if Windows is acting up. If it can't fix something, run Repair Windows image, then check again.",
    "Long repairs can take 15 minutes or more. Don't turn your PC off while they run.",
    "Windows tools opens Windows' own programs in a separate window.",
  ],
  monitor: [
    "Disks shows the health Windows reports and how full each drive is.",
    "Windows errors groups repeated problems. Many are harmless, so look for ones that match when something went wrong.",
    "Hardware tests check your keyboard, mouse and monitor.",
  ],
  history: [
    "Each entry is one set of changes. Undo puts those settings back.",
    "If another program changed a setting since, TuneForge leaves it alone and tells you.",
  ],
  settings: [
    "Theme changes how TuneForge looks.",
    "Automations only run while TuneForge is open or in the tray.",
    "Export saves your choices to a file, which you can import on another PC.",
  ],
};
