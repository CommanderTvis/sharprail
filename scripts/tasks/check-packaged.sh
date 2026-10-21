#!/bin/sh
# `scripts/mise run check:packaged [-- args]`: runs the check gate against the packaged app. The checks are staged beside a copy of the
# bundle's own files, so every product file under test is the one that ships. On Linux the bundle is the AppImage's payload,
# extracted from the image itself (`--linux` is the gate to pass).
set -eu
cd "$(dirname "$0")/../.."
if [ "$(uname -s)" = Linux ]; then
  # The check runs the image, so it is the one built for this host's architecture.
  case "$(uname -m)" in aarch64|arm64) rid=linux-arm64 ;; *) rid=linux-x64 ;; esac
  image="artifacts/SharpRail-$rid.AppImage"
  if [ ! -f "$image" ] || [ ! -x artifacts/linux/checks/SharpRail.Checks ]; then
    echo "No packaged app to check: run scripts/mise run package:linux $rid first." >&2
    exit 1
  fi
  stage=".bench/packaged-$$"
  trap 'rm -rf "$stage"' EXIT
  trap 'exit 130' INT
  trap 'exit 143' TERM
  mkdir -p "$stage/extract"
  # The runtime unpacks the image without FUSE; the payload then stands once as shipped and once with the checks beside it.
  (cd "$stage/extract" && "$OLDPWD/$image" --appimage-extract > /dev/null)
  shipped="$stage/extract/squashfs-root/usr/lib/sharprail"
  mkdir -p "$stage/run"
  cp -R "$shipped/." "$stage/run/"
  artifacts/linux/checks/SharpRail.Checks --stage-packaged artifacts/linux/checks "$stage/run"
  export SHARPRAIL_PACKAGED_CLIENT="$PWD/$shipped"
  SHARPRAIL_REQUIRE_R2R=1 SHARPRAIL_PACKAGED_APP="$PWD/$shipped" "$stage/run/SharpRail.Checks" "$@"
  exit
fi
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
export SHARPRAIL_PACKAGED_CLIENT="$PWD/$bundle/Contents/MacOS"
[ ! -d artifacts/host ] || export SHARPRAIL_PACKAGED_HOST="$PWD/artifacts/host"
artifacts/checks/SharpRail.Checks --stage-packaged artifacts/checks "$stage/SharpRail.app/Contents/MacOS"
SHARPRAIL_REQUIRE_R2R=1 SHARPRAIL_PACKAGED_APP="$PWD/$bundle" "$stage/SharpRail.app/Contents/MacOS/SharpRail.Checks" "$@"
