# Install Wyrmwatch

Download files from the [official releases page](https://github.com/stephenjarrett/Wyrmwatch/releases). Packages include the .NET runtime, license notices, and corresponding source. All downloads include a `.sha256` checksum file.

## Windows x64

Download `Wyrmwatch-0.2.1-win-x64-setup.exe` and run it. Setup installs for your current user without administrator rights and adds a Start menu shortcut. A desktop shortcut is optional. Open Wyrmwatch from the Start menu when ready.

The setup file is currently unsigned. Check that it came from the official repository and compare its SHA-256 value with the release asset. No signing certificate is bundled or installed.

For a portable installation, download `Wyrmwatch-win-x64.zip`, extract the entire folder, and run `Wyrmwatch.exe`. Keep the agent, runtime files, and notices beside it.

## Ubuntu / Debian x64

Download `Wyrmwatch-0.2.1-linux-amd64.deb`. In its download directory:

```sh
sudo apt install ./Wyrmwatch-0.2.1-linux-amd64.deb
```

Open **Wyrmwatch** in the application menu, or run `wyrmwatch`. Run the desktop as your regular user. Installation requires administrative privileges; normal use does not. Ubuntu 24.04 is the tested desktop/package environment. The package declares its shared-library requirements for compatible Debian-based systems.

Other glibc-based Linux distributions can use `Wyrmwatch-linux-x64.tar.gz`:

```sh
tar -xzf Wyrmwatch-linux-x64.tar.gz
cd Wyrmwatch-linux-x64
./Wyrmwatch
```

An X11/XWayland desktop and Avalonia's system libraries are required. Linux manager tests use disposable server fixtures; actual Dragonwilds hosting and game-side save behavior are not certified by those tests.

## First launch

On Servers, choose **Create a server** for a fresh world or **Import a world** to copy an existing world into a new managed server. Both flows use a new installation under your home folder's `WyrmwatchServers` and a separate backup folder under `WyrmwatchBackups`. Review the storage name and complete paths before confirmation.

### Create a server

1. Enter the server/world names and paste your **Dragonwilds Player ID** from the bottom of the game's Settings menu (use its Copy button).
2. Review the storage name, managed locations, default UDP port 7777, generated admin password, and optional world password. The admin password grants game-side administration. The world password controls access and supersedes a password stored in a world; leaving it blank permits anyone who can reach the server to join.
3. Confirm the review to download the dedicated server and write the required settings. Details are recorded in Activity.
4. When setup completes, press **Start** on Servers to create the world. Find it by its exact name in the game's Public Worlds tab. Use Help & diagnostics for firewall/router guidance, and create your first backup after playing.

### Import a world

1. Close the game or stop the server that writes the source save. Choose **Import a world**, select its world `.sav` file, and confirm the source writer is stopped. Local Windows worlds normally live in `%LOCALAPPDATA%\RSDragonwilds\Saved\SaveGames`.
2. Enter the new server's owner ID, server name, port and passwords. **Fallback world name** is used only if the game later creates a fresh world; the copied world's embedded name remains unchanged. The import creates ordinary new-server configuration from the settings you supply.
3. Review the source file and new managed locations, then confirm. Wyrmwatch downloads a fresh server and copies one reviewed `.sav` into its empty SaveGames folder. It preserves the source file and copies no character saves, neighbouring backups, previous configuration or passwords.
4. The imported server stays stopped with automation off. Start it when ready and look for the imported world's exact embedded name in Public Worlds. Keep an independent source backup before playing.

Import checks bounded SAVE/SPUD container framing, source length/modification time/SHA-256, and copied bytes. These checks do not certify game-version compatibility, world contents, player progression, or successful game loading. A changed or locked source requires closing its writer and reviewing the save again. See the [official Dragonwilds world-transfer guide](https://runescapedragonwilds.help.jagex.com/hc/en-gb/articles/45365343055249-Dedicated-Servers-How-to-Guide).

Cancelling before confirmation changes no files. Creation/import never starts a server or enables automation. If preparation fails, downloaded files remain in a separate `.setup-...` folder and its location is reported. A saved connection is added only after successful provisioning. Retry with the same storage name while its final destination remains unused, or go Back to adjust the setup; existing destination folders are never replaced.

Previously saved profiles continue to work, including profiles with external save locations. Their installation and save paths remain read-only in Server settings. Disconnecting an entry preserves its installation, worlds, configuration and backups.

If a completed setup has no saved connection after an interruption, reopen its original workspace and open **Create a server** or **Import a world**, matching the original setup. Select its prepared setup, review the verified settings and choose **Resume prepared setup**. Wyrmwatch checks its workspace ownership receipt and saved-data/launcher hashes, confirms it is stopped, then saves only the connection. It preserves the prepared world and settings without another download or source copy. A missing original source does not prevent resuming its verified copied world. Unknown or modified installations cannot be adopted this way.

## Editing a server

Choose **Edit server** on Servers, or open **Server settings** in the sidebar. Owner ID, fallback world name, passwords and port belong to the selected server; installation and save locations are read-only. Game configuration can be saved only while that server is confirmed stopped. **App settings** contains appearance, language, desktop behavior.

Use **Schedules** on Servers (or **Automation**) for Dragonwilds game updates. Checks default to **60 minutes** and can be set to **30 minutes or longer**. Automatic installation waits until the server has been empty for 60 seconds; unknown player activity defers it. Scheduled backups have their own interval in hours. Wyrmwatch app releases are checked manually on App updates.

## Upgrade and uninstall

Wait for work to finish, close the desktop, and stop its background manager or service before running a replacement installer. Installers refuse replacement/removal while their installed manager is running; they do not force it closed. The Windows installer updates its existing per-user installation; use `apt install ./new-package.deb` for a Debian package upgrade.

Remove Windows installations through **Installed apps**. On Ubuntu/Debian, run `sudo apt remove wyrmwatch`. Uninstalling removes packaged application files and shortcuts, preserving preferences, game installations, saves, and backups. Any service you explicitly configured should be stopped and unregistered first; package removal does not remove or alter your service configuration.

The [in-app updater](updates-and-languages.md) stages portable versions in the workspace and retains the old executable. Opening a staged version does not change an installer's Start menu or application-menu shortcut. Use the new installer to update those installed shortcuts and files. Registered services require an explicit path update.
