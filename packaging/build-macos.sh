#!/usr/bin/env bash
# macOS .app bundles (Apple Silicon and Intel), zipped. Cross-built on Linux, unsigned; NOT tested on macOS.
source "$(dirname "$0")/common.sh"
for RID in osx-arm64 osx-x64; do
  APP="$DIST/macos/$RID/Mod Patch Manager.app"
  rm -rf "$DIST/macos/$RID" && mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  publish "$RID" "$APP/Contents/MacOS"
  cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Mod Patch Manager</string>
  <key>CFBundleDisplayName</key><string>Mod Patch Manager</string>
  <key>CFBundleIdentifier</key><string>io.github.modpatchmanager</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleExecutable</key><string>ModPatchManager</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
  chmod +x "$APP/Contents/MacOS/ModPatchManager"
  cp "$ROOT/packaging/README-untested.txt" "$DIST/macos/$RID/README.txt"
  ZIP="$DIST/ModPatchManager-$VERSION-$RID.zip"
  rm -f "$ZIP"
  (cd "$DIST/macos/$RID" && zip -qry "$ZIP" "Mod Patch Manager.app" README.txt)
  echo "built $ZIP"
done
