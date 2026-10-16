#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
root="$PWD"
mkdir -p .tools/file-icons
while ! mkdir .tools/file-icons/build.lock 2>/dev/null; do sleep 1; done
trap 'rmdir "$root/.tools/file-icons/build.lock"' EXIT HUP INT TERM
fingerprint="$(shasum -a 256 scripts/generate-file-icons.cs scripts/build-file-icons.sh src/SharpRail.Plugins.UI.Kit/FileIcons/FileIconTable.g.cs)"
if [ -f .tools/file-icons/build.fingerprint ] && [ -f .tools/file-icons/assets/file-icons/file.svg ] &&
   [ "$(cat .tools/file-icons/build.fingerprint)" = "$fingerprint" ]; then exit 0; fi
"${DOTNET_HOST_PATH:-.tools/dotnet/dotnet}" run scripts/generate-file-icons.cs -- --assets-only
printf '%s\n' "$fingerprint" > .tools/file-icons/build.fingerprint
