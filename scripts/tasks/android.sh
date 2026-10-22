#!/bin/sh
# `scripts/mise run android [build|run|apk]` builds the Android client (src/SharpRail.Android). `run` also installs it on the connected device or
# emulator and starts it; `apk` builds the signed Release package into artifacts/android.
# Needs the Android SDK (ANDROID_HOME, default ~/Library/Android/sdk). The JDK is the pinned one that
# `scripts/tool jdk` prints; JAVA_HOME, when set, names another JDK 17 or 21 instead.
# With SHARPRAIL_ANDROID_KEYSTORE (a keystore file), SHARPRAIL_ANDROID_KEY_ALIAS and SHARPRAIL_ANDROID_KEY_PASSWORD
# set, the package is signed with that key instead of the machine's debug key.
set -eu
cd "$(dirname "$0")/../.."
export DOTNET_ROOT="$(scripts/tool dotnet)"
sh scripts/tasks/workload.sh android android
sdk="${ANDROID_HOME:-$HOME/Library/Android/sdk}"
[ -d "$sdk/platforms" ] || { echo "No Android SDK at $sdk; set ANDROID_HOME." >&2; exit 1; }
[ -n "${JAVA_HOME:-}" ] || JAVA_HOME="$(scripts/tool jdk)"
[ -x "$JAVA_HOME/bin/javac" ] || { echo "No JDK at $JAVA_HOME; run scripts/mise install, or set JAVA_HOME to a JDK 17 or 21." >&2; exit 1; }
build() {
  if [ -n "${SHARPRAIL_ANDROID_KEYSTORE:-}" ]; then
    # The passwords reach the build by name, so they appear in no command line or log.
    set -- "$@" -p:AndroidKeyStore=true "-p:AndroidSigningKeyStore=$SHARPRAIL_ANDROID_KEYSTORE" \
      "-p:AndroidSigningKeyAlias=$SHARPRAIL_ANDROID_KEY_ALIAS" \
      -p:AndroidSigningKeyPass=env:SHARPRAIL_ANDROID_KEY_PASSWORD -p:AndroidSigningStorePass=env:SHARPRAIL_ANDROID_KEY_PASSWORD
  fi
  dotnet build src/SharpRail.Android "-p:AndroidSdkDirectory=$sdk" "-p:JavaSdkDirectory=$JAVA_HOME" "$@"
}
case "${1:-build}" in
  build) build -c "${SHARPRAIL_CONFIGURATION:-Debug}" ;;
  run)
    build -c "${SHARPRAIL_CONFIGURATION:-Debug}" -t:Install
    "$sdk/platform-tools/adb" shell monkey -p dev.thinkrail.sharprail -c android.intent.category.LAUNCHER 1 >/dev/null
    ;;
  apk)
    build -c Release
    mkdir -p artifacts/android
    cp src/SharpRail.Android/bin/Release/net10.0-android/dev.thinkrail.sharprail-Signed.apk artifacts/android/SharpRail.apk
    echo "artifacts/android/SharpRail.apk"
    ;;
  *) echo 'Usage: scripts/mise run android [build|run|apk]' >&2; exit 1 ;;
esac
