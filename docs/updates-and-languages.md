# App updates and language packs

## Updating Wyrmwatch

The App updates page checks stable releases in `stephenjarrett/Wyrmwatch`. Optional automatic checks run every six hours while the desktop is open. Game updates remain a separate automation setting.

1. Check for a release and review its notes.
2. Download the platform package. Its size and GitHub-published SHA-256 digest must match before extraction.
3. Open the staged version when no operation is running. The current desktop closes, the idle background agent reconnects, and game processes keep running.

Downloads are limited in size, reject linked/traversing archive entries, and extract into a new folder under the workspace's `app-updates` directory. The existing application folder is retained. Launch-at-login is updated when enabled. No running executable is overwritten. A failed verification leaves the current version in place. Staged packages and old versions are not automatically deleted.

For a service or explicitly started standalone agent, the desktop stages the package but does not replace the running host. Stop that agent when idle, update its registered executable path, and start it before opening the new desktop. Keep the same workspace. To return to the previous app, close the idle new manager and open the previous executable; restore preferences from a retained backup if a future version changes their format.

The update page may correctly say there is no newer published release. It does not install arbitrary repository builds or versions with no release digest. Release assets are produced by `.github/workflows/release.yml`; `scripts/package.py` also creates verified local archives after a portable build.

## Community language packs

English is bundled. Settings lets you export a JSON template, translate its string values, and import a community pack. Give it a language ID such as `es` or `pt-BR` and a display name. Keep each `Ui.…` key unchanged. Unknown keys are ignored; missing translations use English.

Packs contain plain text, never executable code or XAML. Imported files are limited to 1 MB and stored under the workspace's `languages` directory. Replacing a pack keeps a `.bak` copy. Save desktop preferences to remember the selected language.

The current catalog covers navigation and static desktop interface text. Runtime diagnostic messages, operation output, dialogs, and the remote dashboard remain English. A language pack is not a promise of fully localized game messages. Contributions can extend the catalog; run `python scripts/extract-strings.py` after adding static interface labels.
