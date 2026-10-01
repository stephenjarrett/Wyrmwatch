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

Choose **Import existing** on Servers, then select its installation folder or paste a launcher path directly into the dialog. Review the detected Saved directory (or select the server's actual Saved directory), and confirm the connection. Reviewing never executes the launcher. Importing preserves the installation and game files. Create a backup before enabling maintenance. Automatic maintenance and background services remain off until you configure them.

For a fresh world, choose **Create Server** on Servers:

1. Review the suggested server/world names and paste your **Dragonwilds Player ID** from the bottom of the game's Settings menu (use its Copy button).
2. Choose an install parent and a new server folder name. A populated parent such as `C:\Games` is supported; its existing contents remain untouched. Review the separate backup location, default UDP port 7777, generated admin password, and optional world password. Blank world passwords allow anyone who can reach the server to join.
3. Review the full installation, save-data and backup paths, then confirm **Create Server**. The wizard downloads the dedicated server and writes the required settings. Details are recorded in Activity.
4. When setup completes, press **Start** on Servers to create the world. Find it by its exact name in the game's Public Worlds tab. Use Help & diagnostics for firewall/router guidance, and create your first backup after playing.

Cancelling before creation changes no files. Creation never starts a server or enables automation. If setup fails, partial downloads are retained in a separate `.setup-...` folder. A saved connection is added only after successful installation and configuration. Retry in the same wizard, or go Back to adjust it; existing target folders are never replaced.

## Editing a server

Choose **Edit server** on Servers, or open **Server settings** in the sidebar. Connection folders, owner ID, world name, passwords and port belong to the selected server. Game configuration can be saved only while that server is confirmed stopped. **App settings** contains appearance, language, desktop behavior.

Use **Schedules** on Servers (or **Automation**) for Dragonwilds game updates. Checks default to **60 minutes** and can be set to **30 minutes or longer**. Automatic installation waits until the server has been empty for 60 seconds; unknown player activity defers it. Scheduled backups have their own interval in hours. Wyrmwatch app releases are checked manually on App updates.

## Upgrade and uninstall

Wait for work to finish, close the desktop, and stop its background manager or service before running a replacement installer. Installers refuse replacement/removal while their installed manager is running; they do not force it closed. The Windows installer updates its existing per-user installation; use `apt install ./new-package.deb` for a Debian package upgrade.

Remove Windows installations through **Installed apps**. On Ubuntu/Debian, run `sudo apt remove wyrmwatch`. Uninstalling removes packaged application files and shortcuts, preserving preferences, game installations, saves, and backups. Any service you explicitly configured should be stopped and unregistered first; package removal does not remove or alter your service configuration.

The [in-app updater](updates-and-languages.md) stages portable versions in the workspace and retains the old executable. Opening a staged version does not change an installer's Start menu or application-menu shortcut. Use the new installer to update those installed shortcuts and files. Registered services require an explicit path update.
