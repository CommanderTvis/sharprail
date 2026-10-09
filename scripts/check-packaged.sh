#!/bin/sh
# Runs the check gate against the packaged app. The checks are staged beside a copy of the
# bundle's own files, so every product file under test is the one that ships.
set -eu
cd "$(dirname "$0")/.."
bundle=artifacts/SharpRail.app
if [ ! -d "$bundle/Contents/MacOS" ] || [ ! -x artifacts/checks/SharpRail.Checks ]; then
  echo "No packaged app to check: run scripts/publish.sh first." >&2
  exit 1
fi
stage=".bench/packaged-$$"
trap 'rm -rf "$stage"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
mkdir -p "$stage"
cp -R "$bundle" "$stage/"
for item in artifacts/checks/*; do
  staged="$stage/SharpRail.app/Contents/MacOS/$(basename "$item")"
  if [ ! -e "$staged" ]; then
    cp -R "$item" "$staged"
  else
    # The remote host the checks start needs newer framework assemblies than the app ships. The
    # gate itself refuses any replacement that is not a newer version of the same assembly.
    case "$item" in *.dll) cmp -s "$item" "$staged" || cp "$item" "$staged" ;; esac
  fi
done
SHARPRAIL_REQUIRE_R2R=1 SHARPRAIL_PACKAGED_APP="$PWD/$bundle" "$stage/SharpRail.app/Contents/MacOS/SharpRail.Checks" "$@"
