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
- A real agent completing a scheduled backup after its desktop parent exits, retaining the next deadline across restart, and avoiding a duplicate backup.
- Authorized HTTP backup/verify/restore, role and server scope restrictions, required restore confirmation, revocation, and owner-only import.
- Windows ZIP and Linux TAR package safety, links/traversal/duplicates/truncation, interrupted downloads, executable permissions, and retention of the previous app.
- Headless UI navigation, themes, focused form stability, and import review/confirmation invalidation after edits.

These checks do not certify real Dragonwilds hosting. Before a release, separately smoke-test native minimize/restore rendering, HTTPS from another device, service startup across reboot, and the switch to a published update using disposable environments. Actual game compatibility and game-side save locations still require validation, especially on Linux. No real server should be used as an automated test fixture.
