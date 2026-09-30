# Windows desktop automation

Use the supported Windows Computer Use APIs and their current skill instructions. Keep the desktop session active and unlocked. Test a packaged app with `--data-dir` pointing to a unique temporary workspace, and import only the repository's disposable `FixtureServer`. Disable automatic app checks, launch at login, and close to tray in that workspace. Never select a real game installation for automation tests.

## Modal text entry

The Windows automation helper tested on 29 September 2026 exposed the main Wyrmwatch window, but not its owned dialogs as separate targetable windows. Its accessibility tree sometimes advertised modal elements that the next input call rejected as unavailable. Sending Ctrl+A to the parent while an Avalonia dialog was focused activated the parent and removed the dialog's keyboard focus. This was an automation-targeting issue; normal main-window text input worked.

The following supported workflow completed native import:

1. If the automation session is stale, reset the JavaScript session, initialize the supported API, and select exactly one freshly returned Wyrmwatch window. Do not invent handles for dialog accessibility nodes.
2. Capture the parent window and inspect the displayed picker. Click the visible **File name** field using coordinates from the parent screenshot and that screenshot's identifier. Refresh and visually confirm its caret. The helper's reported focused element may incorrectly remain **Search Box**.
3. In a separate action, use `type_text` with the absolute disposable launcher path. Refresh and verify the full file-name value. Click **Open** using a fresh screenshot. Do not insert keyboard chords between focusing the field and typing.
4. In Wyrmwatch's import dialog, use `set_value` for the named save and backup fields. If it reports a stale/unavailable element, activate the returned parent window, refresh its accessibility tree, inspect the new field index, then call `set_value` in a separate action. This sequence succeeded for both import fields. Refresh and verify the exact entered path after each field.
5. Use fresh screenshot coordinates for **Review folders**, the save-folder confirmation, and **Import server**. Inspect each resulting state before the next action; review changes the dialog height.

Perform one input action per observation/action cycle. Never reuse indexes, screenshots, or coordinates after a state change. Scroll a control into view before clicking it; the helper may include offscreen controls in its accessibility tree.

This is a verified workaround, not an upstream helper repair or a guarantee that every modal keyboard shortcut works. It requires no application change or Windows security-setting change. For other dialogs, observe and verify rather than assuming the same focus behavior.

## Verified outcome

On the Windows package built from `631ba92`, the workflow imported one disposable installation with an external Saved folder. All 192 installation files and both save/configuration files were unchanged by hash. The backup directory remained absent, automatic updates and backups were off, and no fixture server started. Repeating launcher typing and selection in the same automation session reached the already-connected message and did not create another connection. The live game processes retained their existing IDs and original start times throughout.
