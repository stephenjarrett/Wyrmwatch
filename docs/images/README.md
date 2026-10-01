# README screenshots

These PNGs are direct native Windows client-area captures of Wyrmwatch 0.2.3 after removing the informational sidebar footer labels. They show the real desktop app and its local background manager in unique temporary workspaces, with two explicitly configured, stopped server fixtures. The launchers are non-executable placeholder files; no game engine was started. Automatic updates, scheduled backups, launch at login and background operation are off. No real worlds, credentials or personal manager workspace are shown. Visible temporary installation paths belong only to these fixtures.

- [servers-dark.png](servers-dark.png): Servers workspace in the dark theme.
- [servers-light.png](servers-light.png): the same stopped-server layout in the light theme.
- [resources.png](resources.png): the stopped server's resource guidance, with no invented samples.
- [automation.png](automation.png): game updates, maintenance window, backup frequency and retention, all disabled by default.

To refresh them, prepare only disposable profiles under a new temporary root, with separate installation/save and backup folders. Set the fixture workspace's theme, disable close-to-tray and all automatic actions, then run the packaged app with `--data-dir <fixture-workspace>`. Never point it at an existing installation or click Start, Create, Import or Restore. Capture the owned app window after its profiles have been observed as stopped; navigate only to Resources and Automation. Use a 1240 x 960 client area for Servers and Resources, and 1240 x 1100 for Automation so its Save control also fits. Capture dark/light from separate fixture preferences and close each app normally afterward.

The current captures use native `PrintWindow` on the verified app window, without editing pixels or substituting data in the renderer. Review every image for legibility, clipped controls, secrets and unrelated private paths before committing. Update the version and provenance caption in the root README when refreshing the images. These screenshots do not establish real-game compatibility, screen-reader coverage or every DPI setting.
