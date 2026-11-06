# TuneForge

A private Windows PC optimizer for friends and family.

## Build

Needs the .NET 10 SDK, Node.js 24 and the WebView2 runtime.

```powershell
./build.ps1
```

The app is written to `artifacts\app\Debug\TuneForge.exe`.

## Real-system testing

`tests/sandbox/run-in-sandbox.ps1` applies and undoes every tweak on a real registry inside Windows Sandbox, then runs presets, cleanup and repairs. It refuses to run anywhere else.

Start Sandbox with `wsb start`, map the published CLI to `C:\tf\app`, then run the script with `wsb exec -r System`. Use `wsb` rather than a `.wsb` file, because the Sandbox window can crash on this PC while the VM itself works.

## Layout

| Folder | Purpose |
|---|---|
| `src/TuneForge.Host` | Desktop window, runs without admin rights |
| `src/ui` | React interface |
| `src/TuneForge.Engine` | Admin process, started on request |
| `src/TuneForge.Engine.Core` | Engine logic and pipe server |
| `src/TuneForge.Contracts` | Messages shared by host and engine |
| `src/TuneForge.Cli` | Command-line tool |
| `design-system` | Design rules |

## Rules

- Record every third-party component in `THIRD_PARTY_NOTICES.md`.
