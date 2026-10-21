#!/bin/sh
# Sourced by the build scripts: `acquire_lock <dir>` takes a directory lock with mkdir, writes the owner's PID into it and
# removes it when the script exits. A lock whose owner is gone (killed with SIGKILL, a crashed run, a machine that slept
# through a timeout) is removed instead of waited for forever, and a wait names the holder so a hang is never silent.
# A lock directory without an owner file (written by an older script) counts as stale once it is two minutes old.
acquire_lock() {
  _lock_dir="$1"
  _waited=0
  while ! mkdir "$_lock_dir" 2>/dev/null; do
    _owner="$(cat "$_lock_dir/pid" 2>/dev/null || true)"
    if [ -n "$_owner" ]; then
      if ! kill -0 "$_owner" 2>/dev/null; then
        echo "Removing the stale lock $_lock_dir: its owner (PID $_owner) is no longer running." >&2
        rm -rf "$_lock_dir"
        continue
      fi
    elif [ -d "$_lock_dir" ] && [ -n "$(find "$_lock_dir" -maxdepth 0 -mmin +2 2>/dev/null)" ]; then
      echo "Removing the stale lock $_lock_dir: it names no owner and is older than two minutes." >&2
      rm -rf "$_lock_dir"
      continue
    fi
    if [ "$_waited" -gt 0 ] && [ $((_waited % 30)) -eq 0 ]; then
      echo "Waiting for the lock $_lock_dir held by PID ${_owner:-unknown} ($_waited s so far)." >&2
    fi
    if [ "$_waited" -ge 3600 ]; then
      echo "Gave up after an hour waiting for the lock $_lock_dir held by PID ${_owner:-unknown}; remove it if that build is hung." >&2
      return 1
    fi
    sleep 1
    _waited=$((_waited + 1))
  done
  echo "$$" > "$_lock_dir/pid"
  _held_lock="$_lock_dir"
  trap 'rm -rf "$_held_lock"' EXIT HUP INT TERM
}
