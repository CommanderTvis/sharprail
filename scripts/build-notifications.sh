#!/bin/sh
# Builds the macOS User Notifications bridge the app posts desktop notifications through.
# Usage: build-notifications.sh <output dylib>. The UI project's BuildNotifications target decides when it runs.
set -eu
cd "$(dirname "$0")/.."
[ "$(uname -s)" = Darwin ] || exit 0
out="$1"
mkdir -p "$(dirname "$out")"
clang -dynamiclib -arch "$(uname -m)" -fobjc-arc -mmacosx-version-min=13.0 -O2 -Wall -Wextra -Werror -Wno-unused-parameter \
  src/SharpRail.UI/Native/Notifications.m -framework AppKit -framework Foundation -framework UserNotifications \
  -install_name @rpath/libSharpRailNotifications.dylib -o "$out.partial"
mv "$out.partial" "$out"
