#!/bin/sh
# Builds the macOS User Notifications bridge the app posts desktop notifications through.
set -eu
cd "$(dirname "$0")/.."
[ "$(uname -s)" = Darwin ] || exit 0
root="$PWD"
source=src/SharpRail.UI/Native/Notifications.m
arch="$(uname -m)"
mkdir -p .tools/notifications
# MSBuild may invoke this from multiple referencing projects.
while ! mkdir .tools/notifications/build.lock 2>/dev/null; do sleep 1; done
trap 'rmdir "$root/.tools/notifications/build.lock"' EXIT HUP INT TERM
fingerprint="$arch $(cat "$source" scripts/build-notifications.sh | shasum -a 256)"
if [ -f .tools/notifications/build.fingerprint ] && [ -f .tools/notifications/libSharpRailNotifications.dylib ] &&
   [ "$(cat .tools/notifications/build.fingerprint)" = "$fingerprint" ]; then exit 0; fi
clang -dynamiclib -arch "$arch" -fobjc-arc -mmacosx-version-min=13.0 -O2 -Wall -Wextra -Werror -Wno-unused-parameter \
  "$source" -framework Foundation -framework UserNotifications \
  -install_name @rpath/libSharpRailNotifications.dylib -o .tools/notifications/libSharpRailNotifications.dylib
printf '%s\n' "$fingerprint" > .tools/notifications/build.fingerprint
