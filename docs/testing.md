# Focused verification

Run `dotnet test tests/Wyrmwatch.Core.Tests -c Release` and `dotnet test tests/Wyrmwatch.Desktop.Tests -c Release`. CI runs both suites on Windows and Ubuntu and publishes portable builds.

All server fixtures live in unique temporary directories. Process tests launch the small `FixtureServer` executable supplied by this repository, bind a loopback UDP port, and accept graceful shutdown signals. They never launch SteamCMD or a real game server. Cleanup matches each fixture's exact executable path before terminating any leftover test process.

Coverage includes:

- Continuous empty-server observation, unknown-player deferral, failed updates, and persisted schedules.
- Verified backups, cancellation, locked source files, retention after failure, restore staging, and rollback after a folder-swap failure.
- Shared-port saved connections, overlapping-folder protection, existing-profile/backend import compatibility with external saves, reconnection to previous backups, and rejection before unsafe configuration writes.
- Switching between two disposable servers on the same port only after shutdown, reloaded ownership, and backup/restore isolation on both operating systems.
- Selecting another connection without starting it, refusing starts while another saved server is running or unknown, disconnected-server checks, and maintenance restart enforcement.
- An active operation blocking another action, schedule execution, connection edits, and agent shutdown.
- Installation locks excluding duplicate work while allowing unrelated installations, including an unwritable legacy lock directory on Linux and actionable permission errors.
- A real agent completing a scheduled backup after its desktop parent exits, retaining the next deadline across restart, and avoiding a duplicate backup.
- Authenticated loopback HTTP backup/verify/restore, required restore confirmation, rejection of untrusted origins, and local-only import. Legacy remote settings are ignored and preserved.
- Windows ZIP and Linux TAR package safety, links/traversal/duplicates/truncation, interrupted downloads, executable permissions, and retention of the previous app.
- Headless UI navigation, themes, selected-navigation text contrast, focused form stability, and import review/confirmation invalidation after edits.

## Current desktop workflows

The desktop suite drives mouse clicks and text input against the actual Avalonia controls, connects `AgentClient` to the production loopback HTTP routes, and checks persisted settings and file contents. Dummy Steam and game adapters prevent real downloads or launches. Fixtures are isolated temporary folders.

The current front door provides Create a server and Import a world into separate managed installations. Fixture flows exercise owner/source confirmation, source review, occupied destinations, cancellation, interrupted-download retry, source preservation and changed-source refusal, settings edits, selection between saved servers, single-server start enforcement, graceful stop, backup/restore, disconnect and workspace reopen. Existing-profile/backend import compatibility remains covered separately. Disconnect preserves installation, configuration, worlds and backups.

Desktop tests run serially because Avalonia theme resources and desktop startup options are shared. The same suites run on Windows and Ubuntu in CI. Set `WYRM_TEST_SCREENSHOTS` to an output directory to capture the rendered UI while exercising these flows.

## World-import fixtures

`WorldImportTests` defines synthetic SAVE/SPUD containers in disposable temporary folders. Its cases cover read-only inspection and deterministic review tokens, exact single-file copy without companions, invalid/empty/truncated containers, rejection of GVAS input-settings files, bounded unknown extension chunks, source changes even with unchanged length/timestamp, changes after copying, locked sources, cancellation, occupied destinations, shared source/destination ancestors, and substituted review fields. These fixtures validate top-level container framing and copy integrity, not real Dragonwilds world contents or game-version compatibility.

Provisioning fixtures use dummy Steam/runtime adapters to exercise source-writer acknowledgement and known-server stopped checks, fresh managed configuration, staging failure/retry, source preservation, and no registration until setup succeeds. Existing configuration and character/backup files are not imported with a world. Native file selection, real game loading, multiplayer progression and compatibility with a live world require separate validation.

These descriptions identify regression coverage in the source tree. Test results and platform execution must be checked in the CI or local validation log for the exact revision; adding a fixture is not a claim that an unrun platform check passed.

## Restore and ownership safeguards (0.2.3)

Connected and disconnected profiles reserve their installation and save-data paths. Reconnecting the same installation keeps its identity; changing its installation, launcher or save folder requires a verified stopped server and no pending restore. Scheduled work uses the same overlap validation as manual actions. Operation leases cover both the installation and the canonical Saved tree. Exact matching paths exclude simultaneous commands across workspaces; nested roots have different lock keys, and the lock does not last for a running server's lifetime. Use one workspace for an installation/save tree. These leases do not discover a writer managed by another workspace or an unrelated external server.

Restore writes and flushes a journal before swapping folders. An incomplete or unreadable journal blocks starts and file-changing operations. The Backups page exposes explicit recovery after the server is confirmed stopped. Recovery retains displaced files and verifies the original file inventory and SHA-256 hashes before unblocking; a committed restore is likewise verified before completing journal cleanup. Invalid or damaged recovery state stays blocked for investigation. Graceful Stop remains available during recovery without trying to back up an incomplete tree.

Valid archives can restore into missing or empty Saved folders. Archives containing SaveGames restore the complete world/configuration coverage; newer files absent from the archive are retained in the recovery directory. Configuration-only archives replace Config and explicitly preserve existing SaveGames. Confirmation and recovery-point descriptions show that difference.

Regression coverage includes disconnected save overlap and canonical aliases, rejected manual/scheduled writes, running-profile ownership edits, shared Saved operation leases, every restore swap/commit checkpoint, repeated interruptions during rollback, damaged journals/files, missing/empty targets and large recovery inventories. Checkpoint exceptions emulate abrupt interruption and reopen the persisted journal with fresh service instances; they are not physical power-loss tests.

Windows launcher tests use a dummy launcher that immediately hands off to a shipping child. A non-terminating kernel job preserves ownership through parent exit and manager reopen; unrelated later processes at the same path remain unowned. Linux fixtures cover shell exec and child handoff through a separately owned session. All process tests remain confined to disposable fixture executables.

Headless end-to-end tests exercise the application controls and API, including actual text-input and mouse events. They do not verify Windows/Linux native folder-picker dialogs, Steam network downloads, or game-engine save compatibility. Native Windows mouse input and screen capture were blocked by the environment; those checks remain explicitly unverified.

## Earlier release verification (0.2.0–0.2.1)

Remote-dashboard and browser checks below describe earlier releases. Remote access has been removed in 0.2.2.

Live portable-package checks on 29 September 2026 exercised Windows and Linux imports with external saves, HTTPS backup/verify/restore and access restrictions, agent restart with a fixture server still running, graceful shutdown, and switching two saved servers on the same UDP port. Linux was also checked as a non-root user after another account had created the legacy shared lock directory. A separate Linux container reached the Windows agent over TLS 1.3 with certificate and hostname validation enabled. These used disposable fake servers, with no real server changes.

After reconnecting the active Windows desktop, native capture and interaction worked. The desktop checks exercised navigation, a focused unsaved field across background refreshes, light/dark themes, minimize/restore, fixture start and graceful stop, backup selection and verification, operation history, and the no-new-release state. Captured restore frames preserved the expected colors and layout; point-in-time screenshots cannot exclude a very brief flash. The light-theme selected-navigation contrast defect found in that run has a focused regression test.

The complete native Windows import flow subsequently passed using a fresh disposable workspace: typed launcher selection, explicit external save and backup paths, folder review, confirmation, and import. All 194 installation/save fixture files retained their SHA-256 hashes, no backup directory was created, no fixture process started, and both automation flags remained off. A second typed selection without resetting the automation session reached the already-connected guard and left exactly one saved connection. See [Windows desktop automation](windows-desktop-testing.md) for the helper's modal-targeting workaround; this is not a fix to the helper itself.

A separate Ubuntu 24.04 WSL2 distribution subsequently passed all 21 portable-package assertions as a regular user. The native X11 desktop was exercised under Xvfb/Openbox: rendering, GTK file selection, reviewed external-save import, start/graceful stop with two recovery points, light/dark themes, and minimize/restore. The Debian package passed fresh installation, refusal to replace/remove a running manager, successful reinstall after shutdown, and removal with preferences/world files preserved. This tests Linux desktop behavior on a virtual display, not every graphics driver or desktop environment.

The Linux user service was installed without starting it, explicitly enabled for the test account, and checked for fixture preservation across service stop/restart. Restarting only the isolated distribution with lingering enabled started the service again without the desktop. An overdue backup ran once, the next deadline persisted, and the world was unchanged. An initial harness attempt put its workspace in `/tmp`, which Ubuntu clears on startup; the corrected restart fixture used a dedicated disposable directory on persistent storage.

Windows installer lifecycle checks passed on disposable GitHub-hosted runners: complete payload, fresh installation, refusal to replace a running agent, reinstallation, removal, and preservation of a workspace sentinel. The low-privilege LocalService check also passed: service startup, fixture control, retaining the game across service stop/restart, ownership recovery, graceful shutdown, and unchanged saves. `scripts/test-windows-service.ps1` is restricted to hosted CI and refuses to run on a user's machine. Its state assertions wait for the manager's periodic observation rather than assuming immediate refresh.

Chrome in the isolated Ubuntu environment passed Viewer sign-in, recovery-point viewing, and sign-out without browser errors. Only read actions were exposed, and browser storage was empty after sign-out. The fixture certificate fingerprint was verified and trusted only in the disposable Linux account; certificate verification was not bypassed. This browser used loopback. The earlier container-to-Windows HTTPS check exercised a separate client, but neither is a physical-device firewall test.

The [v0.2.0 release pipeline](https://github.com/stephenjarrett/Wyrmwatch/actions/runs/36653865716) passed both platforms and published setup EXE, Debian, ZIP, and TAR packages with checksums. Downloaded installers matched both the release asset digests and accompanying SHA-256 files. The Windows application updater discovered the actual release, verified and extracted its ZIP, and confirmed the bundled source revision matched the release tag.

A controlled Linux client built with assembly version 0.1.99 discovered the published 0.2.0 release through the native UI, downloaded and verified it, and confirmed the switch. The new desktop and agent retained the running fixture's PID, executable path, and kernel start token. Settings and world hashes were unchanged, and the new manager stopped the fixture gracefully. The previous executable remained available and reopened the same workspace after the updated app was closed. The virtual desktop harness must keep its WSL session alive and reap the exited desktop process; initial harness attempts did not do both. The successful run used a persistent session, and normal closing/reopening was checked with Openbox.

These checks do not certify real Dragonwilds hosting. Remaining deployment checks include a physical second device's browser/trust/firewall setup, Windows service startup across a full OS reboot, and native Windows UI handoff to the published update. The last Windows UI attempt returned `GetCursorPos failed: Access is denied` before interaction; download/extraction and earlier native desktop flows passed, but that final UI handoff is not claimed. Actual game compatibility and game-side save locations still require validation, especially on Linux. No real server should be used as an automated test fixture.
