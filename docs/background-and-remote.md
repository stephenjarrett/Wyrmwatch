# Background operation and remote access

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

## Remote dashboard

Remote access starts off. On the Remote access page, choose an unused port (default 8843), enable HTTPS, and apply. Applying restarts an idle desktop-owned agent. For a registered service or standalone agent, restart it manually when idle. A port or certificate failure keeps local management available and shows the error on this page.

No public relay, tunnel, firewall rule, or router mapping is created. Other devices need a route to the host, such as the same LAN or your existing VPN. The displayed machine-name URL may need your host's LAN address if name resolution is unavailable.

The agent creates a self-signed certificate in the private workspace. Export/open its public `.cer` file and verify its SHA-256 fingerprint against the desktop display before trusting it on another device. Never share `remote-certificate.pfx`, `agent.json`, or the workspace. Replacing the certificate is a manual operation: disable remote access, stop the idle agent, retain the previous certificate files privately, then let a new certificate be generated. Review browser trust again after replacement.

Create a named key with a 1–365 day lifetime. The secret is shown once; only its hash is stored. Restrict it to the selected server or grant access to all connections. Revocation takes effect on the next request; it cannot cancel an operation already accepted.

| Role | Permissions |
| --- | --- |
| Viewer | Read server status and backup metadata |
| Operator | Viewer permissions; start, stop, restart, create backups, and check game updates |
| Maintainer | Operator permissions; apply game updates and verify/restore backups |

Only the local desktop can edit connection paths, game configuration, remote settings, or access keys. Remote status excludes process paths and activity-log contents. Restore requires the stopped server and typed server-name confirmation, then uses the same backup/retained-folder safeguards as the desktop.

The dashboard keeps keys in tab memory, not URLs or browser storage. Signing out or reloading requires the key again. The footer offers the package's matching source archive and AGPL license.
