#!/bin/sh
# Restores the Zig packages hosted on Codeberg (neither GitHub nor Ghostty's deps.files.ghostty.org CDN, which are fetched upstream by design) into Zig's global cache before a native build, so CI does not
# depend on codeberg.org at build time. scripts/zig-deps.lock pins a mirror archive (a release
# asset of this repository, built by scripts/mise run zig-deps:mirror) by SHA-256 and lists each package with its upstream URL.
# Zig fetches nothing for a package whose p/ entry exists, whatever its URL says.
# Usage: zig-deps.sh <dir|tgz>   dir = Zig 0.14 (extracted p/<hash>), tgz = Zig 0.16 (p/<hash>.tar.gz)
# Only absent packages are restored, so a warm cache needs no network. A missing or corrupt mirror falls back to the
# upstream URLs Zig uses; if one of those is unreachable too, this fails naming the package and URL.
set -eu
[ -z "${ZIG_DEPS_NO_RESTORE:-}" ] || exit 0
form="${1:?Usage: zig-deps.sh <dir|tgz>}"
lock="$(dirname "$0")/zig-deps.lock"
cache="${ZIG_GLOBAL_CACHE_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/zig}"
mkdir -p "$cache/p"
missing="$cache/zig-deps-missing.$$"
trap 'rm -rf "$missing" "$cache/zig-deps-extract.$$"' EXIT HUP INT TERM
awk -v form="$form" -v cache="$cache" '$1 == "pkg" && index($3, form) {
  if (form == "dir") path = cache "/p/" $2; else path = cache "/p/" $2 ".tar.gz"
  if (system("test -e \"" path "\"") != 0) print $2, $4 }' "$lock" > "$missing"
[ -s "$missing" ] || exit 0

field() { awk -v k="$1" '$1 == k { print $2 }' "$lock"; }
repo="$(field repo)"; tag="$(field tag)"; asset="$(field asset)"; sha="$(field sha256)"
archive="$cache/$asset"
sum() { shasum -a 256 "$1" | cut -d' ' -f1; }
fetched=
if [ -f "$archive" ] && [ "$(sum "$archive")" = "$sha" ]; then fetched=1
elif curl -fsSL --retry 4 --retry-all-errors --connect-timeout 20 -o "$archive.partial" "https://github.com/$repo/releases/download/$tag/$asset" 2>/dev/null \
  && [ "$(sum "$archive.partial")" = "$sha" ]; then mv "$archive.partial" "$archive"; fetched=1
else rm -f "$archive.partial"; fi

if [ -n "$fetched" ]; then
  tmp="$cache/zig-deps-extract.$$"; mkdir -p "$tmp"
  tar -xzf "$archive" -C "$tmp"
  while read -r pkg url; do
    if [ "$form" = dir ]; then entry="$pkg"; else entry="$pkg.tar.gz"; fi
    [ -e "$cache/p/$entry" ] || [ ! -e "$tmp/p/$entry" ] || mv "$tmp/p/$entry" "$cache/p/$entry" 2>/dev/null || true
  done < "$missing"
  echo "Restored Zig packages from $asset into $cache/p."
  exit 0
fi

echo "::warning title=Zig dependency mirror unavailable::$asset (release $tag of $repo) could not be downloaded or failed its SHA-256 check; Zig will fetch the packages from their upstream URLs."
status=0
while read -r pkg url; do
  case "$url" in
    git+*) probe="${url#git+}"; probe="${probe%%#*}/info/refs?service=git-upload-pack" ;;
    *) probe="$url" ;;
  esac
  if ! curl -fsS -r 0-0 --retry 2 --connect-timeout 20 -o /dev/null "$probe" 2>/dev/null; then
    echo "::error title=Zig dependency unreachable::Package $pkg is not mirrored and its upstream $url is unreachable. Publish the mirror (scripts/mise run zig-deps:mirror) or retry when the host is back." >&2
    status=1
  fi
done < "$missing"
exit $status
