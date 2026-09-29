# Focused verification

Run `dotnet test tests/Dragonwilds.Core.Tests -c Release` and `dotnet test tests/Wyrmwatch.Desktop.Tests -c Release`. CI runs both suites on Windows and Ubuntu and publishes portable builds.

All server fixtures live in unique temporary directories. Process tests launch the small `FixtureServer` executable supplied by this repository, bind a loopback UDP port, and accept graceful shutdown signals. They never launch SteamCMD or a real game server. Cleanup matches each fixture's exact executable path before terminating any leftover test process.

Coverage includes:

- Continuous empty-server observation, unknown-player deferral, failed updates, and persisted schedules.
- Verified backups, cancellation, locked source files, retention after failure, restore staging, and rollback after a folder-swap failure.
- Shared-port saved connections, overlapping-folder protection, read-only import with external saves, reconnection to previous backups, and rejection before unsafe configuration writes.
- Switching between two disposable servers on the same port only after shutdown, reloaded ownership, and backup/restore isolation on both operating systems.
- Selecting another connection without starting it, refusing starts while another saved server is running or unknown, disconnected-server checks, and maintenance restart enforcement.
- An active operation blocking another action, schedule execution, connection edits, and agent shutdown.
- Installation locks excluding duplicate work while allowing unrelated installations, including an unwritable legacy lock directory on Linux and actionable permission errors.
- A real agent completing a scheduled backup after its desktop parent exits, retaining the next deadline across restart, and avoiding a duplicate backup.
- Authorized HTTP backup/verify/restore, role and server scope restrictions, required restore confirmation, revocation, and owner-only import.
- Windows ZIP and Linux TAR package safety, links/traversal/duplicates/truncation, interrupted downloads, executable permissions, and retention of the previous app.
- Headless UI navigation, themes, selected-navigation text contrast, focused form stability, and import review/confirmation invalidation after edits.

Live portable-package checks on 29 September 2026 exercised Windows and Linux imports with external saves, HTTPS backup/verify/restore and access restrictions, agent restart with a fixture server still running, graceful shutdown, and switching two saved servers on the same UDP port. Linux was also checked as a non-root user after another account had created the legacy shared lock directory. A separate Linux container reached the Windows agent over TLS 1.3 with certificate and hostname validation enabled. These used disposable fake servers, with no real server changes.

After reconnecting the active Windows desktop, native capture and interaction worked. The desktop checks exercised navigation, a focused unsaved field across background refreshes, light/dark themes, minimize/restore, fixture start and graceful stop, backup selection and verification, operation history, and the no-new-release state. Captured restore frames preserved the expected colors and layout; point-in-time screenshots cannot exclude a very brief flash. The light-theme selected-navigation contrast defect found in that run has a focused regression test. The native file picker opened and canceled correctly, but automated text input did not reach it, so the complete native import flow remains unverified; import review and API behavior are separately covered.

These checks do not certify real Dragonwilds hosting. Before a release, separately smoke-test native file selection, HTTPS browser access from a physical second device, service startup across reboot, and the switch to a published update using disposable environments. Actual game compatibility and game-side save locations still require validation, especially on Linux. No real server should be used as an automated test fixture.
