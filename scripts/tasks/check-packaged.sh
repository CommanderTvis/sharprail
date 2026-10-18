#!/bin/sh
# `scripts/mise run check:packaged [-- args]`: runs the check gate against the packaged app. The checks are staged beside a copy of the
# bundle's own files, so every product file under test is the one that ships.
set -eu
cd "$(dirname "$0")/../.."
bundle=artifacts/SharpRail.app
if [ ! -d "$bundle/Contents/MacOS" ] || [ ! -x artifacts/checks/SharpRail.Checks ]; then
  echo "No packaged app to check: run scripts/mise run package:macos first." >&2
  exit 1
fi
stage=".bench/packaged-$$"
trap 'rm -rf "$stage"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
mkdir -p "$stage"
cp -R "$bundle" "$stage/"
artifacts/checks/SharpRail.Checks --stage-packaged artifacts/checks "$stage/SharpRail.app/Contents/MacOS"
SHARPRAIL_REQUIRE_R2R=1 SHARPRAIL_PACKAGED_APP="$PWD/$bundle" "$stage/SharpRail.app/Contents/MacOS/SharpRail.Checks" "$@"
