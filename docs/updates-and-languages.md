# App updates and language packs

## Updating Wyrmwatch

The App updates page checks stable releases in `stephenjarrett/Wyrmwatch`. Checks run only when you press the check button; there is no automatic app-release watcher. Game updates remain a separate automation setting.

1. Check for a release and review its notes.
2. Press **Download and update**. Wyrmwatch downloads the platform package, verifies its size and GitHub-published SHA-256 digest, then restarts automatically with the same workspace. Game processes keep running.
3. If a server operation is still running or the app cannot reopen, the verified package is retained. Use **Restart to update** once the manager is idle; no second download is needed.

On Windows, installations established with 0.2.8 or later also require an Ed25519-signed catalog embedded-key verification, exact release/version binding, and a second package check immediately before handoff. The verified setup program replaces application files at the existing installation location after the app and background manager exit gracefully. An independent, verified backup and helper restore the previous application if installation or verification fails. Desktop and Start menu shortcuts retain their arguments and explicitly preserve the active workspace. Game installations, saves, backups and credentials remain outside application recovery. The installer shows brief progress and reopens the same workspace automatically.

Versions through 0.2.7 used portable workspace copies and cannot acquire the corrected installer flow retroactively. Run the 0.2.8 Windows setup program once, with the old manager closed and idle, to establish the installed update path. Portable Windows copies need that same setup migration. Existing custom shortcut workspace arguments are retained; for a manual migration using a custom workspace, launch setup with `/UPDATEWORKSPACE="<absolute-workspace-path>"` to reopen that workspace automatically.

An older running ownership helper inside the installation being replaced locks its runtime files. Migration refuses before replacing files and asks you to explicitly stop that server when safe, then rerun setup. It cannot relocate loaded legacy binaries. A helper in a separate older portable folder does not block the new installation. Future Windows starts use a 4.5 KiB query-only helper in a hash-verified `process-support` workspace cache, independent of the app runtime. It exits when its game job is empty and never stops the game. Existing Windows .NET Framework 4.8 support is used; no runtime or system setting is installed. An excessively long workspace path is refused before game code runs. Cached helper versions are retained while old jobs might still use them.

Linux portable updates retain the existing bounded download/extraction flow, reject linked/traversing entries and preserve the old application folder. Launch-at-login is refreshed when enabled. Staged portable packages and old versions are not automatically deleted.

For a service or explicitly started standalone agent, the desktop stages the package and requires a manual handoff. On Windows, stop the Wyrmwatch service when idle, run the verified installer at the existing installation location, then restart the service and open the desktop with the same workspace. For a portable Linux host, stop it when idle, update its registered executable path to the verified extracted version, and restart it. No service is automatically stopped, registered or enabled.

The update page may correctly say there is no newer published release. It does not install arbitrary repository builds or versions with no release digest. Release assets are produced by `.github/workflows/release.yml`; `scripts/package.py` also creates verified local archives after a portable build.

## Community language packs

English is bundled. Settings lets you export a JSON template, translate its string values, and import a community pack. Give it a language ID such as `es` or `pt-BR` and a display name. Keep each `Ui.…` key unchanged. Unknown keys are ignored; missing translations use English.

Packs contain plain text, never executable code or XAML. Imported files are limited to 1 MB and stored under the workspace's `languages` directory. Replacing a pack keeps a `.bak` copy. Save desktop preferences to remember the selected language.

The current catalog covers navigation and static desktop interface text. Runtime diagnostic messages, operation output, dialogs remain English. A language pack is not a promise of fully localized game messages. Contributions can extend the catalog; run `python scripts/extract-strings.py` after adding static interface labels.
