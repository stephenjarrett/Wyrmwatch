# App updates and language packs

## Updating Wyrmwatch

The App updates page checks stable releases in `stephenjarrett/Wyrmwatch`. Checks run only when you press the check button; there is no automatic app-release watcher. Game updates remain a separate automation setting.

1. Check for a release and review its notes.
2. Press **Download and update**. Wyrmwatch downloads the platform package, verifies its size and GitHub-published SHA-256 digest, then restarts automatically with the same workspace. Game processes keep running.
3. If a server operation is still running or the app cannot reopen, the verified package is retained. Use **Restart to update** once the manager is idle; no second download is needed.

Downloads are limited in size, reject linked/traversing archive entries, and extract into a new folder under the workspace's `app-updates` directory. The existing application folder is retained. Launch-at-login is updated when enabled. On Windows, an existing Wyrmwatch desktop shortcut is retargeted to the new version; its arguments and a backup of the previous shortcut are retained. Other shortcuts are left alone. No running executable is overwritten. A failed verification leaves the current version in place. Staged packages and old versions are not automatically deleted.

For a service or explicitly started standalone agent, the desktop stages the package but does not replace the running host. Stop that agent when idle, update its registered executable path, and start it before opening the new desktop. Keep the same workspace. To return to the previous app, close the idle new manager and open the previous executable; restore preferences from a retained backup if a future version changes their format.

The update page may correctly say there is no newer published release. It does not install arbitrary repository builds or versions with no release digest. Release assets are produced by `.github/workflows/release.yml`; `scripts/package.py` also creates verified local archives after a portable build.

## Community language packs

English is bundled. Settings lets you export a JSON template, translate its string values, and import a community pack. Give it a language ID such as `es` or `pt-BR` and a display name. Keep each `Ui.…` key unchanged. Unknown keys are ignored; missing translations use English.

Packs contain plain text, never executable code or XAML. Imported files are limited to 1 MB and stored under the workspace's `languages` directory. Replacing a pack keeps a `.bak` copy. Save desktop preferences to remember the selected language.

The current catalog covers navigation and static desktop interface text. Runtime diagnostic messages, operation output, dialogs remain English. A language pack is not a promise of fully localized game messages. Contributions can extend the catalog; run `python scripts/extract-strings.py` after adding static interface labels.
