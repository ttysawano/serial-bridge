#!/usr/bin/env bash
set -euo pipefail

# connect-pty.sh
# Create a PTY (pseudo-serial device) in WSL and bridge it to serial-bridge TCP.
#
# Usage:
#   ./wsl/connect-pty.sh
#   ./wsl/connect-pty.sh --port 7000 --link /tmp/obc-tty
#   ./wsl/connect-pty.sh --host 127.0.0.1 --port 7000 --link /tmp/obc-tty
#
# Notes:
# - Requires: socat
# - Default host tries:
#   1) 127.0.0.1
#   2) /etc/resolv.conf nameserver (often Windows host in WSL2)

HOST=""
PORT="7000"
LINK="/tmp/obc-tty"
SLEEP_SEC="1"

usage() {
  cat <<USAGE
Usage: $0 [--host HOST] [--port PORT] [--link PATH] [--sleep SEC]

Options:
  --host HOST   TCP host of serial-bridge (default: auto-detect)
  --port PORT   TCP port (default: 7000)
  --link PATH   PTY symlink path to create (default: /tmp/obc-tty)
  --sleep SEC   sleep between reconnect attempts (default: 1)
  -h, --help    show this help
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --host) HOST="${2:-}"; shift 2;;
    --port) PORT="${2:-}"; shift 2;;
    --link) LINK="${2:-}"; shift 2;;
    --sleep) SLEEP_SEC="${2:-}"; shift 2;;
    -h|--help) usage; exit 0;;
    *) echo "[connect-pty] unknown arg: $1" >&2; usage; exit 2;;
  esac
done

if ! command -v socat >/dev/null 2>&1; then
  echo "[connect-pty] socat not found. Install in WSL:" >&2
  echo "  sudo apt update && sudo apt install -y socat" >&2
  exit 1
fi

auto_hosts=()
if [[ -n "$HOST" ]]; then
  auto_hosts+=("$HOST")
else
  auto_hosts+=("127.0.0.1")
  # add Windows host candidate (often nameserver in WSL2)
  if [[ -r /etc/resolv.conf ]]; then
    ns="$(awk '/^nameserver[[:space:]]+/{print $2; exit}' /etc/resolv.conf || true)"
    if [[ -n "${ns:-}" ]]; then
      auto_hosts+=("$ns")
    fi
  fi
fi

echo "[connect-pty] PTY link   : $LINK"
echo "[connect-pty] TCP target : (auto) ${auto_hosts[*]}:${PORT}"
echo "[connect-pty] Tip: your app should open $LINK as the serial device."

# Main reconnect loop
while true; do
  for h in "${auto_hosts[@]}"; do
    echo "[connect-pty] trying TCP $h:$PORT ..."
    # socat will print the actual /dev/pts/N it created (debug -d -d).
    # PTY options:
    # - link=<path>    create symlink at PATH
    # - raw,echo=0     raw mode, no local echo
    # - waitslave      wait until some process opens the slave
    # - ignoreof       don't exit just because the slave side closes (helps during app restart)
    #
    # TCP side is a simple connect. If it drops, socat often exits; the outer loop restarts it.
    socat -d -d \
      "PTY,link=${LINK},raw,echo=0,waitslave,ignoreof" \
      "TCP:${h}:${PORT}" \
      && exit 0

    rc=$?
    echo "[connect-pty] socat exited (rc=$rc). reconnect in ${SLEEP_SEC}s ..."
    sleep "$SLEEP_SEC"
  done
done
