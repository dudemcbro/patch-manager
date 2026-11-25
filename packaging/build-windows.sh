#!/usr/bin/env bash
# Windows x64 build, zipped. Cross-built on Linux; NOT tested on Windows.
source "$(dirname "$0")/common.sh"
OUT="$DIST/windows/ModPatchManager"
rm -rf "$OUT" && mkdir -p "$OUT"
publish win-x64 "$OUT"
cp "$ROOT/packaging/README-untested.txt" "$OUT/README.txt"
ZIP="$DIST/ModPatchManager-$VERSION-win-x64.zip"
rm -f "$ZIP"
(cd "$DIST/windows" && zip -qr "$ZIP" ModPatchManager)
echo "built $ZIP"
