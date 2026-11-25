#!/usr/bin/env bash
# Linux x86_64 AppImage. Needs appimagetool on PATH (or APPIMAGETOOL=/path/to/appimagetool).
source "$(dirname "$0")/common.sh"
TOOL="${APPIMAGETOOL:-$(command -v appimagetool || true)}"
[ -n "$TOOL" ] || { echo "appimagetool not found; set APPIMAGETOOL" >&2; exit 1; }

APPDIR="$DIST/linux/ModPatchManager.AppDir"
rm -rf "$APPDIR" && mkdir -p "$APPDIR/usr/bin"
publish linux-x64 "$APPDIR/usr/bin"

cp "$ROOT/packaging/assets/modpatchmanager.svg" "$APPDIR/modpatchmanager.svg"
cat > "$APPDIR/modpatchmanager.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Mod Patch Manager
Comment=Merge WoW 3.3.5a patches into one
Exec=ModPatchManager
Icon=modpatchmanager
Categories=Game;Utility;
Terminal=false
DESKTOP
cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/ModPatchManager" "$@"
APPRUN
chmod +x "$APPDIR/AppRun" "$APPDIR/usr/bin/ModPatchManager"

OUT="$DIST/ModPatchManager-$VERSION-x86_64.AppImage"
ARCH=x86_64 "$TOOL" --no-appstream "$APPDIR" "$OUT"
echo "built $OUT"
