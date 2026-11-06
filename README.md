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

`tests/sandbox/run-apps-in-sandbox.ps1` removes a real built-in app and runs winget install, update and uninstall. Headless Sandbox has no logged-in user, so run it from a scheduled task under a throwaway local admin. It sets up winget itself and needs internet. The Store source doesn't work in Sandbox, so reinstalling from the Store has to be tested on a real PC.

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
