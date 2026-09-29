# Wyrmwatch

A focused desktop manager for RuneScape: Dragonwilds dedicated servers. Built in C# on .NET 10 and Avalonia, with Windows and Linux interfaces from one codebase.

This is a standalone implementation, not a fork. It is an unofficial community tool and is not affiliated with Jagex.

## MVP

- Modern dashboard, CPU/memory graphs, free disk space, dark/light/system themes.
- Connect existing installations in place; multiple saved server connections.
- Install new servers into empty folders with SteamCMD.
- Start, graceful stop, restart, and per-installation process tracking.
- Compare installed and available Steam builds before updating.
- Automatic updates wait for a continuously observed empty server for 60 seconds. Unknown player activity defers maintenance. Optional daily maintenance window.
- Scheduled and manual ZIP backups of the selected Saved/SaveGames and Saved/Config folders, SHA-256 verification, and per-profile retention.
- Guided restore: verify, back up the current state, stage files, retain previous folders, and leave the server stopped.
- Persisted operation history, live activity log, read-only diagnostics, and official setup help.
- Launch at login. Windows close-to-tray keeps schedules running; on Linux, closing exits because tray availability differs by desktop.

Automatic updates and scheduled backups start **off**. Opening the app never adopts, installs, launches, or stops an unconfigured game server. No Delete Server action is included.

## Use an existing server

1. Choose **Connect existing server** and select its launcher (`RSDragonwildsServer.exe` on Windows, the server launch script on Linux).
2. In Settings, check the **save-data folder**. The default is `RSDragonwilds/Saved` inside the installation. If this server uses your user-profile data, explicitly select that server's real Saved folder instead. Wyrmwatch does not guess or combine unrelated player saves.
3. Save the connection, then create a manual backup. Saving connection preferences does not rewrite game configuration.
4. Enable schedules when ready. Run only one server-management application with automatic maintenance enabled for the installation.

A server started elsewhere can be monitored and backed up. Wyrmwatch will not send it shutdown signals without a recorded owned process identity. Stop it using its existing controls at a convenient time, then start it through Wyrmwatch for managed shutdown. No forced-stop fallback is enabled.

Config edits require the server to be stopped, preserve unrelated sections/admin lists, and retain the previous file. Read the [official Dragonwilds server guide](https://runescapedragonwilds.help.jagex.com/hc/en-gb/articles/45365343055249-Dedicated-Servers-How-to-Guide) for owner IDs, networking, world saves, and game-side administration. The game chooses the newest save; restore moves the old active save folder into a retained recovery directory so newer saves do not override the selected recovery point.

## Run

The portable builds include the .NET runtime. Extract the entire build folder; do not copy just the executable.

Windows: run `Wyrmwatch.exe`. Linux desktop: `chmod +x Wyrmwatch`, then `./Wyrmwatch`. Linux requires an X11/XWayland desktop and the [Avalonia system dependencies](https://docs.avaloniaui.net/docs/supported-platforms). SteamCMD requires its distribution-specific 32-bit runtime libraries. Linux builds are provided as previews until verified with a real Linux game installation.

Command-line options:

```text
--demo                Illustrative dashboard; server actions disabled
--data-dir PATH       Separate manager preferences/history (not a server sandbox)
--minimized           Start minimized
```

Default manager state is stored in the platform's local application-data directory under `Wyrmwatch`. This is separate from the old Python manager. Neither its settings nor its running processes are migrated automatically.

## Maintenance and recovery limits

Schedules run while Wyrmwatch is open or in the Windows tray, with the computer awake. Closing the app fully pauses schedules and leaves the game running. Missed jobs run once after reopening. A Windows service/headless daemon, remote administration, localization packs, and manager self-updates are outside this MVP.

Live backups contain files already written to disk; they cannot capture unsaved in-memory progress. Updates take a second backup after shutdown. Backups are stored outside the installation. Only Wyrmwatch-marked archives for the same profile and installation are pruned; recovery folders are never automatically deleted. Keep an additional backup on a separate drive.

Player counts are inferred conservatively from the current session's Unreal log. Missing startup markers, an unreadable/oversized log, or unmatched leave events defer automatic updates. An external start or new player can still race the final state check; the game does not expose a transactional maintenance lock.

On Windows, an isolated helper verifies the target process identity and console members before sending Ctrl+C. Linux uses SIGINT only for recorded owned game processes. If graceful shutdown fails, maintenance stops. A failed install or build verification leaves the server stopped for investigation.

Backups include configuration passwords. Treat the backup folder as private. Wyrmwatch does not open firewall ports or expose remote-control endpoints.

## Development

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet build Wyrmwatch.slnx
dotnet test tests/Dragonwilds.Core.Tests
dotnet test tests/Wyrmwatch.Desktop.Tests
dotnet run --project src/Wyrmwatch.Desktop -- --demo
./scripts/build.ps1 -Runtime win-x64
./scripts/build.ps1 -Runtime linux-x64
```

The MVP checks exercise backup integrity and restore preservation, update failures and deferral, configuration preservation, a disposable Windows console shutdown, and two UI smoke flows. All game-data fixtures are temporary. No tests use a real server. GitHub Actions runs the same checks and packages both targets.

`Dragonwilds.Core` is independent of the UI. `Dragonwilds.Windows` currently contains both process adapters (Windows and Linux); `Dragonwilds.Signal` is the Windows console helper. `Wyrmwatch.Desktop` contains the Avalonia interface. The current Linux support needs desktop and game-process validation on Linux; compiling a Linux target is not a substitute for that check.
