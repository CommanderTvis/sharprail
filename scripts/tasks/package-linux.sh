#!/bin/sh
set -eu
# `scripts/mise run package:linux`: publishes the Linux client and checks (self-contained, ReadyToRun) and packs the client into
# artifacts/SharpRail-linux-x64.AppImage (or, given `linux-arm64`, artifacts/SharpRail-linux-arm64.AppImage) with the pinned appimagetool and type 2 runtime. The native libraries cross-compile with Zig
# during the build, so the managed publish also works on macOS; appimagetool runs on Linux only. Set SHARPRAIL_APPDIR_ONLY=1
# elsewhere to stop after assembling artifacts/linux/AppDir. With SHARPRAIL_GPG_KEY_ID set (a key in the GPG keyring), the image
# carries an embedded signature that AppImageLauncher and appimaged verify.
cd "$(dirname "$0")/../.."
dotnet_cmd="${DOTNET:-.tools/dotnet/dotnet}"
rid="${1:-linux-x64}"
case "$rid" in
  linux-x64) appimage_arch=x86_64 ;;
  linux-arm64) appimage_arch=aarch64 ;;
  *) echo "Usage: package-linux.sh [linux-x64|linux-arm64] (got '$rid')" >&2; exit 1 ;;
esac
output=artifacts/linux
image="artifacts/SharpRail-$rid.AppImage"
# appimagetool and its runtime are per-architecture binaries, so the image is built on a host of its own architecture.
if [ "$(uname -s)" = Linux ] && [ "${SHARPRAIL_APPDIR_ONLY:-}" != 1 ]; then
  case "$(uname -m)" in x86_64|amd64) host=linux-x64 ;; aarch64|arm64) host=linux-arm64 ;; *) host="$(uname -m)" ;; esac
  [ "$host" = "$rid" ] || { echo "Building $rid.AppImage needs a $rid host; this one is $host. SHARPRAIL_APPDIR_ONLY=1 assembles the AppDir only." >&2; exit 1; }
fi
if [ "$(uname -s)" != Linux ] && [ "${SHARPRAIL_APPDIR_ONLY:-}" != 1 ]; then
  echo 'appimagetool runs on Linux; set SHARPRAIL_APPDIR_ONLY=1 to assemble artifacts/linux/AppDir here.' >&2
  exit 1
fi
# Publish copies only newer files; stale outputs would otherwise survive into the package.
rm -rf "$output" "$image"
# The compiler gate, the natives and the fixture plugin the checks install.
"$dotnet_cmd" build SharpRail.slnx -c Release
# The About screen's version stamp, as the nightly names its builds: UTC date and short commit.
stamp="$(date -u +%Y%m%d)-$(git rev-parse --short HEAD)"
# One locked restore (packages.lock.json lists every shipped RID) that also fetches the runtime and Crossgen packs;
# the publishes below reuse it, since a `-r` restore would not match the lock's RuntimeIdentifiers.
"$dotnet_cmd" restore SharpRail.slnx -p:SelfContained=true -p:PublishReadyToRun=true
"$dotnet_cmd" publish src/SharpRail.UI -c Release -r "$rid" --self-contained true --no-restore \
  -p:PublishReadyToRun=true -p:PublishReadyToRunComposite=true -p:SharpRailBuildStamp="$stamp" -o "$output/client"
# The packaged gate combines checks with the client's shipped components, so the checks publish apart and non-composite.
"$dotnet_cmd" publish tests/SharpRail.Checks -c Release -r "$rid" --self-contained true --no-restore \
  -p:PublishReadyToRun=true -p:PublishReadyToRunComposite=false -o "$output/checks"
cp -R tests/SharpRail.Checks/bin/Release/net10.0/plugin-fixture "$output/checks/"

app="$output/AppDir"
payload="$app/usr/lib/sharprail"
mkdir -p "$payload" "$app/usr/share/doc/sharprail" "$app/usr/share/icons/hicolor/96x96/apps"
cp -R "$output/client/." "$payload/"
# Debug symbols and API documentation do not belong in an image users download.
find "$payload" \( -name '*.pdb' -o -name '*.xml' \) -type f -delete
chmod +x "$payload/SharpRail.UI"
cp THIRD-PARTY-NOTICES.md "$app/usr/share/doc/sharprail/"
cp src/SharpRail.Plugins.UI.Kit/Assets/Icons/brand.png "$app/sharprail.png"
cp "$app/sharprail.png" "$app/.DirIcon"
cp "$app/sharprail.png" "$app/usr/share/icons/hicolor/96x96/apps/sharprail.png"
cat > "$app/sharprail.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=SharpRail
Comment=Workbench for projects, Git worktrees, specs and agent terminals
Exec=SharpRail.UI
Icon=sharprail
Terminal=false
Categories=Development;IDE;
StartupWMClass=SharpRail.UI
DESKTOP
cat > "$app/AppRun" <<'APPRUN'
#!/bin/sh
here="$(dirname "$(readlink -f "$0")")"
# .NET aborts at startup without ICU unless globalization is invariant; minimal images lack it, so fall back to invariant mode.
if [ -z "${DOTNET_SYSTEM_GLOBALIZATION_INVARIANT:-}" ] && ! PATH="$PATH:/sbin:/usr/sbin" ldconfig -p 2>/dev/null | grep -q 'libicuuc\.so'; then
  export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
fi
exec "$here/usr/lib/sharprail/SharpRail.UI" "$@"
APPRUN
chmod +x "$app/AppRun"

if [ "$(uname -s)" != Linux ]; then
  echo "Assembled $app; run this task on Linux to build the AppImage."
  exit 0
fi
# These tools exist for Linux only, so the task installs them here rather than depending on their bootstrap.
sh scripts/mise run bootstrap:linux
tool=.tools/appimagetool/appimagetool.AppImage
runtime=.tools/appimage-runtime/runtime
[ -f "$tool" ] && [ -f "$runtime" ] || { echo 'bootstrap:linux did not install appimagetool.' >&2; exit 1; }
# The store is read-only and appimagetool needs neither FUSE nor a writable home: it unpacks itself and writes only the image.
# The task argument (the runtime identifier) is not an appimagetool argument.
set --
[ -z "${SHARPRAIL_GPG_KEY_ID:-}" ] || set -- --sign --sign-key "$SHARPRAIL_GPG_KEY_ID"
ARCH="$appimage_arch" APPIMAGE_EXTRACT_AND_RUN=1 "$tool" "$@" --no-appstream --runtime-file "$runtime" --comp zstd \
  --mksquashfs-opt -Xcompression-level --mksquashfs-opt 19 "$app" "$image"
chmod +x "$image"
echo "Built $image"
