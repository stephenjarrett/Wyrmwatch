# Background operation

The desktop and agent use the same workspace, saved connections, process ownership records, and safety checks. Closing the window never stops the game. Start with automatic maintenance off while checking your connection and backup location.

Disconnecting removes the active connection and disables its schedules. Its previous identity is retained privately so reconnecting the same installation can recognize its existing backups. Reconnecting starts with schedules off; review the save-data and backup folders again before enabling them.

## Keep schedules running

In Settings, enable **Keep the background manager running**, then save desktop preferences. The agent continues after the desktop closes and reconnects when you reopen it. It runs as your account; logging out or restarting the computer can end it. Launch at login starts the desktop again. For operation without a desktop session, explicitly register a service.

The agent can also be run directly:

```text
agent/Wyrmwatch.Agent --workspace /absolute/private/workspace
```

On Windows use `agent\Wyrmwatch.Agent.exe`. Without a desktop-parent argument it stays running until you stop it. Do not run it as root or SYSTEM. Use the account that owns your server files and the exact same workspace as the desktop. Diagnostics shows that workspace path.

## Windows service

The portable package includes `service/install-windows-service.ps1`. From an elevated PowerShell session:

```powershell
./service/install-windows-service.ps1 -ApplicationDirectory 'C:\Apps\Wyrmwatch' -Workspace 'C:\Users\you\AppData\Local\Wyrmwatch' -Credential (Get-Credential)
```

Choose your server-owning account. Windows must grant that account **Log on as a service**. Registration does not start the service. Turn background mode off and quit the desktop when idle, then run `Start-Service Wyrmwatch`. The service will start automatically on subsequent boots. Reopen the desktop with the same workspace to manage it.

Use `Stop-Service Wyrmwatch` when idle before changing the service's application path or unregistering it. Do not force-terminate an in-progress backup, restore, or update. The game remains running when the agent exits normally. Service-mode start/stop of a real game still needs deployment validation; the automated checks use disposable fixtures.

## Linux user service

```sh
bash service/install-linux-service.sh /absolute/app/folder /absolute/private/workspace
```

The script writes a user unit without enabling or starting it. Turn background mode off and quit the desktop when idle, then run `systemctl --user enable --now wyrmwatch`. Use `systemctl --user status wyrmwatch` and `journalctl --user -u wyrmwatch` for diagnostics. To start without logging in, explicitly enable lingering for your user through your distribution's `loginctl` setup. Service stop leaves the child game process running; do not change the supplied `KillMode=process` setting without understanding that consequence.

## Local access only

The agent binds an authenticated HTTP endpoint only on loopback. Remote control, external HTTPS listeners and access-key management are unavailable. Existing remote settings and certificate files are preserved but not used.
