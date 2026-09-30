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

Choose **Import existing server**, select that server's launcher and actual Saved directory, review the folders, and confirm the connection. Importing preserves the installation and game files. Create a backup before enabling maintenance. Automatic maintenance, background services, and remote access remain off until you configure them.

## Upgrade and uninstall

Wait for work to finish, close the desktop, and stop its background manager or service before running a replacement installer. Installers refuse replacement/removal while their installed manager is running; they do not force it closed. The Windows installer updates its existing per-user installation; use `apt install ./new-package.deb` for a Debian package upgrade.

Remove Windows installations through **Installed apps**. On Ubuntu/Debian, run `sudo apt remove wyrmwatch`. Uninstalling removes packaged application files and shortcuts, preserving preferences, game installations, saves, and backups. Any service you explicitly configured should be stopped and unregistered first; package removal does not remove or alter your service configuration.

The [in-app updater](updates-and-languages.md) stages portable versions in the workspace and retains the old executable. Opening a staged version does not change an installer's Start menu or application-menu shortcut. Use the new installer to update those installed shortcuts and files. Registered services require an explicit path update.
