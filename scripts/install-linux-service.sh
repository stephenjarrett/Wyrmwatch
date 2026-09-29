#!/usr/bin/env bash
set -eu
if [ "$#" -ne 2 ]; then
  printf '%s\n' 'Usage: install-linux-service.sh /absolute/app/directory /absolute/workspace'
  exit 2
fi
app_root=$(realpath -- "$1")
workspace_root=$(realpath -- "$2")
agent_path="$app_root/agent/Wyrmwatch.Agent"
test -f "$agent_path" || { printf '%s\n' 'Extract a complete Linux portable build first.'; exit 1; }
case "$agent_path$workspace_root" in *'"'*|*'%'*|*'\'*|*'
'*) printf '%s\n' 'Paths containing quotes, percent signs, backslashes, or newlines are not supported.'; exit 1;; esac
unit_dir="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
mkdir -p "$unit_dir"
unit="$unit_dir/wyrmwatch.service"
test ! -e "$unit" || { printf '%s\n' 'The user service already exists; its configuration was preserved.'; exit 1; }
chmod u+x -- "$agent_path"
cat > "$unit" <<EOF
[Unit]
Description=Wyrmwatch background manager
After=network-online.target

[Service]
Type=simple
ExecStart="$agent_path" --workspace "$workspace_root"
Restart=on-failure
RestartSec=15
TimeoutStopSec=3000
KillMode=process
UMask=0077

[Install]
WantedBy=default.target
EOF
systemctl --user daemon-reload
printf '%s\n' 'Service file installed but not enabled or started.' 'Quit the desktop with background mode off, then run: systemctl --user enable --now wyrmwatch'
printf '%s\n' 'For boot without login, enable lingering for your account explicitly. Do not run the manager as root.'
