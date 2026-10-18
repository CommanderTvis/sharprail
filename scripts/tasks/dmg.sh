#!/bin/sh
# `scripts/mise run dmg [path]`: packs artifacts/SharpRail.app (from package:macos) into a disk image with the usual Applications shortcut.
# The path argument chooses the image; the default is artifacts/SharpRail.dmg.
set -eu
cd "$(dirname "$0")/../.."
bundle=artifacts/SharpRail.app
image="${1:-artifacts/SharpRail.dmg}"
[ -d "$bundle" ] || { echo "No $bundle; run scripts/mise run package:macos first." >&2; exit 1; }
staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT HUP INT TERM
# ditto keeps the bundle's symlinks and signature intact.
ditto "$bundle" "$staging/SharpRail.app"
ln -s /Applications "$staging/Applications"
rm -f "$image"
hdiutil create -volname SharpRail -srcfolder "$staging" -fs HFS+ -format UDZO -quiet "$image"
echo "$image"
