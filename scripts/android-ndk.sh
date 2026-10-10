#!/bin/sh
# Installs the pinned Android NDK into the checkout's tools and prints its path.
# The Android client's native libraries (terminal state, editor) are built with its clang.
set -eu
cd "$(dirname "$0")/.."
version=r27d
sha256=e69092f9d2bfa5d1199039980a14eb91c03cc971ab5c6968fc08a8e6b84e7bb7
root="$PWD/.tools/android-ndk"
ndk="$root/$version"
mkdir -p "$root"
# MSBuild may invoke this from several native builds at once.
while ! mkdir "$root/install.lock" 2>/dev/null; do sleep 1; done
trap 'rmdir "$root/install.lock"' EXIT HUP INT TERM
if [ ! -f "$ndk/source.properties" ]; then
  case "$(uname -s)" in Darwin) host=darwin ;; Linux) host=linux ;; *) echo 'The Android NDK needs macOS or Linux.' >&2; exit 1 ;; esac
  rm -rf "$root/partial"
  mkdir "$root/partial"
  curl -fL --retry 3 "https://dl.google.com/android/repository/android-ndk-$version-$host.zip" -o "$root/partial/ndk.zip"
  if [ "$host" = darwin ]; then
    printf '%s  %s\n' "$sha256" "$root/partial/ndk.zip" | shasum -a 256 -c - >/dev/null
  fi
  unzip -q "$root/partial/ndk.zip" -d "$root/partial"
  mv "$root/partial/android-ndk-$version" "$ndk"
  rm -rf "$root/partial"
fi
printf '%s\n' "$ndk"
