#!/usr/bin/env bash
# Builds every package into dist/. Windows and macOS builds are untested.
set -euo pipefail
DIR="$(dirname "$0")"
"$DIR/build-appimage.sh"
"$DIR/build-windows.sh"
"$DIR/build-macos.sh"
