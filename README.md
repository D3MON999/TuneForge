# TuneForge

A private Windows PC optimizer for friends and family.

## Build

Needs the .NET 10 SDK, Node.js 24 and the WebView2 runtime.

```powershell
./build.ps1
```

The app is written to `artifacts\app\Debug\TuneForge.exe`.

## Layout

| Folder | Purpose |
|---|---|
| `src/TuneForge.Host` | Desktop window, runs without admin rights |
| `src/ui` | React interface |
| `src/TuneForge.Engine` | Admin process, started on request |
| `src/TuneForge.Engine.Core` | Engine logic and pipe server |
| `src/TuneForge.Contracts` | Messages shared by host and engine |
| `design-system` | Design rules |

## Rules

- Record every third-party component in `THIRD_PARTY_NOTICES.md`.
