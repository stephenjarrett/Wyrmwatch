<img src="src/Wyrmwatch.Desktop/Assets/wyrmwatch.svg" alt="Wyrmwatch logo" width="72" align="right" />

# Wyrmwatch

[![Windows and Linux checks](https://github.com/stephenjarrett/Wyrmwatch/actions/workflows/build.yml/badge.svg?branch=master)](https://github.com/stephenjarrett/Wyrmwatch/actions/workflows/build.yml)

A focused desktop manager for RuneScape: Dragonwilds dedicated servers. Built in C# on .NET 10 and Avalonia, with Windows and Linux interfaces from one codebase.

[**Download for Windows & Linux**](https://github.com/stephenjarrett/Wyrmwatch/releases/latest) · [Installation guide](docs/installing.md) · [Import a world](#import-a-world) · [Contribute](CONTRIBUTING.md)

An unofficial community tool, not affiliated with Jagex. Licensed [AGPL-3.0-only](LICENSE).

![Servers workspace with two disposable servers and controls for the selected server](docs/images/servers-dark.png)

*Native Windows screenshots of Wyrmwatch 0.2.3 with two stopped, disposable server fixtures. No live game data is shown; automatic maintenance is off.*

<details>
<summary><strong>Light theme</strong></summary>

![Servers workspace in the light theme](docs/images/servers-light.png)

</details>

## Features

- Servers workspace with a saved-server list, CPU/memory graphs, free disk space, dark/light/system themes.
- Create a fresh world or import one world .sav into a new managed server; choose from saved servers and run one at a time.
- Guided setup with standard managed locations, owner ID help, generated admin password, and a review before the SteamCMD download.
- Start, graceful stop, restart, and per-installation process tracking.
- Compare installed and available Steam builds before updating.
- Automatic updates wait for a continuously observed empty server for 60 seconds. Unknown player activity defers maintenance. Optional daily maintenance window.
- Scheduled and manual ZIP backups of the selected Saved/SaveGames and Saved/Config folders, SHA-256 verification, and per-profile retention.
- Guided restore with explicit world/configuration coverage, retained previous folders, interrupted-restore recovery, and start blocking until recovery is verified.
- Persisted operation history, a filterable game-log viewer, read-only diagnostics, and official setup help.
- Launch at login, Windows close-to-tray, and an opt-in background manager that continues schedules after the desktop closes.
- Optional Windows service and Linux user-service setup for unattended operation.
- Manual checks for new Wyrmwatch releases, SHA-256-verified downloads, and switching to a staged version while retaining the previous app.
- Import/export community language packs with English fallback, without replacing focused forms.
- Disconnect saved server connections without deleting their installation, saves, or backups.

Automatic game updates, scheduled backups, and background operation start **off**. Opening the app never adopts, installs, launches, or stops an unconfigured game server. Disconnect preserves the installation, saves and backups.

## Set up a managed server

### Create a server

Choose **Create a server** on Servers. Enter the server/world names and your Dragonwilds Player ID from the bottom of the in-game Settings menu. Review UDP port 7777, the generated admin password, and the optional world password. Wyrmwatch proposes a storage name and keeps new installations under your home folder's `WyrmwatchServers`, with backups separately under `WyrmwatchBackups`. The wizard shows complete installation, save-data and backup paths before confirmation. It downloads and configures a fresh dedicated server, then leaves it stopped; press **Start** when ready. The game creates the fresh world on first start.

### Import a world

1. Close the game or stop the server that writes the source world, then choose **Import a world**. Select the world's `.sav` file and confirm its writer is stopped. Local Windows worlds normally live in `%LOCALAPPDATA%\RSDragonwilds\Saved\SaveGames`.
2. Enter the new server's name, owner Player ID, port and passwords. Review the managed locations and source file. Import downloads a fresh dedicated server and copies exactly the reviewed `.sav`; the original remains in place. Character files, configuration, neighbouring backups and other worlds are not copied.
3. Confirm import. Source length, modification time and SHA-256 are checked again before and after copying, and copied bytes are verified. The new server remains stopped with schedules off until you choose to start it.

Import preserves the world's embedded name. **Fallback world name** applies only if the game later creates a fresh world; it does not rename the imported world. Server name identifies the new server connection. Your supplied settings govern its owner and access: the admin password grants game-side administration, and the world password supersedes any password stored in the world. A blank world password permits anyone who can reach the server to join. Keep passwords private.

World inspection checks bounded SAVE/SPUD container framing. It does not certify game-version compatibility, world contents, player progression, or successful game loading. Follow the [official world-transfer guide](https://runescapedragonwilds.help.jagex.com/hc/en-gb/articles/45365343055249-Dedicated-Servers-How-to-Guide) and keep an independent backup before playing.

If setup files finished publishing but the connection could not be saved, reopen the same workspace and choose the matching Create/Import wizard. Select the prepared setup offered there, review its verified original settings and choose **Resume prepared setup**. A workspace-owned receipt must match the installation, launcher and complete saved-data inventory. Resume only registers the stopped connection; it does not download again, rewrite settings or copy the world again. The already-copied world can be resumed even if its original source has changed or disappeared. Unknown folders, altered files and conflicting saved profiles remain blocked.

Cancelling before confirmation changes no files. A failure during preparation retains its separate `.setup-...` staging folder and reports its location; a saved connection is added only after successful provisioning. Review a changed source again, then retry with the same storage name while its final destination remains unused, or choose another. Existing destination folders are never overwritten. Previously saved profiles, including external save locations, remain supported; installation and save paths are read-only in Server settings.

A server started elsewhere can be monitored and backed up. Wyrmwatch will not send it shutdown signals without a recorded owned process identity. Stop it using its existing controls at a convenient time, then start it through Wyrmwatch for managed shutdown. No forced-stop fallback is enabled.

Keep as many saved server connections as needed, and choose one from the list. Selecting an entry changes the view; it does not start it or stop the current server. Stop the running server before starting another. Desktop actions and maintenance restarts all check the other saved connections immediately before launching; unknown process status blocks a start. Disconnecting a running connection does not bypass this check. These safeguards apply to connections known to this workspace, including disconnected entries; Wyrmwatch does not control other manager workspaces or unconfigured installations.

Saved servers may reuse the same game port because Wyrmwatch runs one at a time. Keep separate installation and save-data folders for each connection. Overlapping game/save paths remain blocked, and backups must stay outside every connected or disconnected server's game/save folders. A common external backup destination is allowed because archives are scoped to their server. Maintenance actions run one at a time across the manager, and updating a stopped server leaves it stopped.

Config edits require the server to be stopped, preserve unrelated sections/admin lists, and retain the previous file. Read the [official Dragonwilds server guide](https://runescapedragonwilds.help.jagex.com/hc/en-gb/articles/45365343055249-Dedicated-Servers-How-to-Guide) for owner IDs, networking, world saves, and game-side administration. The game chooses the newest save. Backups identifies each recovery point's coverage: a world restore replaces SaveGames and Config, retaining newer files separately; a configuration-only restore preserves existing worlds. An interrupted restore blocks starts and file-changing actions until explicit recovery on Backups verifies the retained state. Keep the server stopped during recovery; unverifiable state remains blocked with its files preserved.

## Run

Download the [latest release](https://github.com/stephenjarrett/Wyrmwatch/releases/latest). Windows has a per-user setup `.exe`; Ubuntu/Debian has a `.deb` package. Portable ZIP and TAR downloads remain available. See [installation, upgrades, and removal](docs/installing.md).

The portable builds include one .NET runtime shared by the desktop and background manager, so no separate .NET installation is needed. Debugging symbols are excluded from downloads. Extract the entire build folder; do not copy just the executable.

Windows: run `Wyrmwatch.exe`. Linux desktop: `chmod +x Wyrmwatch agent/Wyrmwatch.Agent`, then `./Wyrmwatch`. Linux requires an X11/XWayland desktop and the [Avalonia system dependencies](https://docs.avaloniaui.net/docs/supported-platforms). SteamCMD requires its distribution-specific 32-bit runtime libraries. Linux builds are provided as previews until verified with a real Linux game installation.

Command-line options:

```text
--demo                Illustrative dashboard; server actions disabled
--data-dir PATH       Separate manager preferences/history (not a server sandbox)
--minimized           Start minimized
```

Manager preferences and operation history are stored in the platform's local application-data directory under `Wyrmwatch`. Server connections are configured explicitly. The desktop starts a separate background manager for the workspace and connects over an authenticated loopback endpoint. Only one manager can own a workspace.

See [background operation](docs/background.md) for service setup. See [app updates and language packs](docs/updates-and-languages.md) for the update workflow and translation templates.

## Maintenance and recovery limits

Schedules need the computer awake and the background manager running. By default it exits with the desktop. Enable **Keep the background manager running** under App settings and save desktop preferences to continue after closing the window. A registered service or explicitly started standalone agent runs independently. Missed jobs run once after the manager returns. Closing the manager leaves the game running.

Live backups contain files already written to disk; they cannot capture unsaved in-memory progress. Updates take a second backup after shutdown. Backups are stored outside the installation. Only Wyrmwatch-marked archives for the same profile and installation are pruned; recovery folders are never automatically deleted. Keep an additional backup on a separate drive.

Player counts are inferred conservatively from the current session's Unreal log. Missing startup markers, an unreadable/oversized log, or unmatched leave events defer automatic updates. An external start or new player can still race the final state check; the game does not expose a transactional maintenance lock.

On Windows, an isolated helper verifies the target process identity and console members before sending Ctrl+C. Linux uses SIGINT only for recorded owned game processes. If graceful shutdown fails, maintenance stops. A failed install or build verification leaves the server stopped for investigation.

Backups include configuration passwords. Keep the workspace and backup folder private. Wyrmwatch never changes firewall or router rules. The manager API only listens on the local loopback interface. Remote control is unavailable; legacy access settings are ignored and preserved.

## Development

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet build Wyrmwatch.slnx
dotnet test tests/Wyrmwatch.Core.Tests
dotnet test tests/Wyrmwatch.Desktop.Tests
dotnet run --project src/Wyrmwatch.Desktop -- --demo
./scripts/build.ps1 -Runtime win-x64
./scripts/build.ps1 -Runtime linux-x64
```

Focused checks cover backup integrity and restore preservation, update failures and deferral, configuration preservation, a disposable Windows console shutdown, local API authentication, background lifecycle, app-package integrity, desktop navigation/theme/language flows, and UI-to-API create/import/edit/start/stop/backup/restore/remove workflows with dummy Steam and game adapters. Native OS folder pickers, actual Steam downloads and game-engine behavior require separate live validation. All game-data fixtures are temporary. No tests use a real server. GitHub Actions runs the same checks and packages both targets. Publishing a `v*` tag invokes the release workflow; it rejects a tag that does not match the project version.

`Wyrmwatch.Core` is independent of the UI. `Wyrmwatch.Platform` contains both process adapters (Windows and Linux); `Wyrmwatch.Signal` is the Windows console helper. `Wyrmwatch.Agent` runs local maintenance. `Wyrmwatch.Desktop` contains the Avalonia interface. Linux desktop, package, service restart, and process control checks run against disposable fixtures; actual game hosting still requires separate validation.

## License and contributions

Wyrmwatch is open source under the [GNU Affero General Public License, version 3](LICENSE) (`AGPL-3.0-only`). You may use, modify, fork, and redistribute it, including commercially, subject to the license's source-sharing and notice requirements. See [NOTICE](NOTICE) for the copyright and license grant.

Anyone can contribute through issues and pull requests; see [CONTRIBUTING.md](CONTRIBUTING.md). Contributions back to this repository are encouraged, not required by the license. Distributing covered binaries requires providing the corresponding source under the AGPL's terms. If you modify the program and let users interact with it remotely over a network, section 13 requires offering those users the corresponding source. Third-party components retain their [own licenses](THIRD-PARTY-NOTICES.md).

Portable builds include `Wyrmwatch-source.zip` containing the project source and build scripts, plus `SOURCE.md` with the build revision. The application is provided without warranty.
