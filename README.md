# TuneForge

All-in-one Windows optimizer with tweaks you can undo, cleanup, app management, game boosts and network tools.

## Build

Needs the .NET 10 SDK, Node.js 24 and the WebView2 runtime.

```powershell
./build.ps1
```

The app is written to `artifacts\app\Debug\TuneForge.exe`.

## Release

```powershell
./release.ps1
```

This runs every test, then writes the signed installer to `artifacts\release`. It needs Inno Setup 6 and the signing key at `%LOCALAPPDATA%\TuneForgeTools\release-key.pem`. Keep a backup of that key and never commit it, because installed copies only accept updates signed with it. To release, publish the `.exe` and `.exe.sig` to a GitHub release tagged `vX.Y.Z` in the public releases repository.

## Real-system testing

`tests/sandbox/run-in-sandbox.ps1` applies and undoes every tweak on a real registry inside Windows Sandbox, then runs presets, cleanup and repairs. It refuses to run anywhere else.

Start Sandbox with `wsb start`, map the published CLI to `C:\tf\app`, then run the script with `wsb exec -r System`. Use `wsb` rather than a `.wsb` file, because the Sandbox window can crash on this PC while the VM itself works.

`tests/sandbox/run-apps-in-sandbox.ps1` removes a real built-in app and runs winget install, update and uninstall. Headless Sandbox has no logged-in user, so run it from a scheduled task under a throwaway local admin. It sets up winget itself and needs internet. The Store source doesn't work in Sandbox, so reinstalling from the Store has to be tested on a real PC.

`tests/sandbox/run-network-in-sandbox.ps1` changes DNS for real and checks restore and automatic. Run it with `wsb exec -r System`.

`tests/sandbox/run-automation-in-sandbox.ps1` checks the memory cleaner and Start with Windows. Run it with `wsb exec -r System`.

`tests/sandbox/run-gaming-in-sandbox.ps1` boosts a stand-in game and checks everything is undone. Run it from a scheduled task under a throwaway standard user. That user needs the Performance Log Users group to run a task, and has no admin rights.

`tests/sandbox/run-install-in-sandbox.ps1` installs the real installer, checks it, then uninstalls it. Map `artifacts\release` to `C:\tf\installer` and run it with `wsb exec -r System`.

## Layout

| Folder | Purpose |
|---|---|
| `src/TuneForge.Host` | Desktop window, runs without admin rights |
| `src/ui` | React interface |
| `src/TuneForge.Engine` | Admin process, started on request |
| `src/TuneForge.Engine.Core` | Engine logic and pipe server |
| `src/TuneForge.Contracts` | Messages shared by host and engine |
| `src/TuneForge.Cli` | Command-line tool |
| `installer` | Inno Setup script |
| `tools/TuneForge.ReleaseTool` | Makes and uses the update signing key, not shipped |
| `design-system` | Design rules |

## Rules

- Record every third-party component in `THIRD_PARTY_NOTICES.md`.

## Releasing

1. Commit and push in GitHub Desktop.
2. Double-click `Release.cmd`. It raises the version, tests, builds and signs, then opens the release page and the folder with the files.
3. Drag in the three `TuneForge-Setup` files and click **Publish release**.

Installed copies offer the update within 6 hours. Add `[important]` to the release notes to bring it on screen on PCs where TuneForge is only in the tray.
