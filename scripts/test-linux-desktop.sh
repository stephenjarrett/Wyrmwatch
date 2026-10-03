#!/usr/bin/env bash
# Run under xvfb-run. The published application uses only its isolated demo workspace.
set -euo pipefail
app=$(realpath "$1")
image=$(realpath -m "$2")
fixture=$(mktemp -d -t wyrmwatch-desktop-package-XXXXXXXX)
"$app/Wyrmwatch" --demo --data-dir "$fixture/workspace" >"$fixture/desktop.log" 2>&1 &
application_pid=$!
cleanup() {
    kill -TERM "$application_pid" 2>/dev/null || true
    wait "$application_pid" 2>/dev/null || true
}
trap cleanup EXIT
window=''
for attempt in {1..100}; do
    if ! kill -0 "$application_pid" 2>/dev/null; then
        cat "$fixture/desktop.log"
        echo 'Published desktop exited before rendering.' >&2
        exit 1
    fi
    window=$(xwininfo -root -tree | awk '/"Wyrmwatch/ { print $1; exit }')
    if [[ -n "$window" ]]; then break; fi
    sleep 0.2
done
if [[ -z "$window" ]]; then
    cat "$fixture/desktop.log"
    echo 'Published desktop did not create a window.' >&2
    exit 1
fi
sleep 2
import -window "$window" "$image"
test -s "$image"
kill -0 "$application_pid"
echo "PASS: published Linux desktop rendered its isolated demo window. Screenshot: $image"
