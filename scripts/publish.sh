#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
dotnet_cmd="${DOTNET:-.tools/dotnet/dotnet}"
rid="${1:-osx-arm64}"
# Publish copies only newer files; stale outputs (for example a native library from a
# downgraded timestamp) would otherwise survive into the package.
rm -rf artifacts/ui artifacts/host artifacts/checks artifacts/SharpRail.app
"$dotnet_cmd" publish src/SharpRail.UI -c Release -r "$rid" --self-contained true \
  -p:PublishReadyToRun=true -p:PublishReadyToRunComposite=false -o artifacts/ui
"$dotnet_cmd" publish src/SharpRail.Host.Remote -c Release -r "$rid" --self-contained true \
  -p:PublishReadyToRun=true -p:PublishReadyToRunComposite=false -o artifacts/host
"$dotnet_cmd" publish tests/SharpRail.Checks -c Release -r "$rid" --self-contained true \
  -p:PublishReadyToRun=true -p:PublishReadyToRunComposite=false -o artifacts/checks
# The fixture plugin builds straight into the checks' build output; the plugin checks install it from beside the executable.
cp -R tests/SharpRail.Checks/bin/Release/net10.0/plugin-fixture artifacts/checks/
if [ "$rid" = osx-arm64 ] || [ "$rid" = osx-x64 ]; then
  bundle=artifacts/SharpRail.app
  mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
  for item in artifacts/ui/*; do
    name="$(basename "$item")"
    case "$name" in
      ghostty|terminfo)
        mkdir -p "$bundle/Contents/Resources/$name"
        cp -R "$item/." "$bundle/Contents/Resources/$name/" ;;
      *) cp -R "$item" "$bundle/Contents/MacOS/" ;;
    esac
  done
  cp THIRD-PARTY-NOTICES.md "$bundle/Contents/Resources/"
  cp -R licenses "$bundle/Contents/Resources/"
  cp src/SharpRail.Plugins.UI.Kit/Assets/Fonts/Geist-OFL.txt "$bundle/Contents/Resources/"
  cp src/SharpRail.Plugins.UI.Kit/Assets/Fonts/JetBrainsMono-OFL.txt "$bundle/Contents/Resources/"
  cat > "$bundle/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>SharpRail.UI</string>
<key>CFBundleIdentifier</key><string>dev.thinkrail.sharprail</string>
<key>CFBundleName</key><string>SharpRail</string>
<key>CFBundleVersion</key><string>1</string>
<key>CFBundleShortVersionString</key><string>0.2.0</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST
  codesign --force --deep --sign - "$bundle"
fi
