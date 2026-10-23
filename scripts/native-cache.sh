#!/bin/sh
# Sourced by the native build scripts that keep finished builds in the machine-wide cache ($SHARPRAIL_CACHE_DIR, which
# mise.toml defines; the fallback here is the same path). A result is a directory named by a key that hashes everything
# its build reads, so checkouts on different branches each keep their own and a new worktree reuses one. Results are
# built in `<result>.partial` and published by rename, and a checkout's copy replaces each output by rename, so neither a
# concurrent reader nor an interrupted build ever sees a half-written library. Callers source scripts/lock.sh too and hold
# acquire_lock on the cache entry while they write.

# sharprail_cache_dir: prints the cache root, creating it on first use; on macOS a new root is excluded from Time Machine.
sharprail_cache_dir() {
  _cache_root="${SHARPRAIL_CACHE_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/sharprail}"
  if [ ! -d "$_cache_root" ]; then
    mkdir -p "$_cache_root"
    [ "$(uname -s)" != Darwin ] || tmutil addexclusion "$_cache_root" >/dev/null 2>&1 || true
  fi
  printf '%s\n' "$_cache_root"
}

# cache_key <text>: a short hash of a fingerprint, used as a result directory name.
cache_key() { printf '%s' "$1" | shasum -a 256 | cut -c1-16; }

# publish_result <result>: moves the finished <result>.partial to <result>.
publish_result() {
  rm -rf "$1"
  mv "$1.partial" "$1"
}

# install_output <result> <output-directory> <name>...: copies each named file or directory of a result into the output
# directory, replacing the previous one by rename.
install_output() {
  _result="$1"
  _output="$2"
  shift 2
  mkdir -p "$_output"
  for _name in "$@"; do
    rm -rf "$_output/$_name.partial"
    cp -R "$_result/$_name" "$_output/$_name.partial"
    rm -rf "${_output:?}/${_name:?}"
    mv "$_output/$_name.partial" "$_output/$_name"
  done
}
