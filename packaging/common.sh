#!/usr/bin/env bash
# Shared settings for the packaging scripts. Run them from anywhere; output goes to dist/.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/Mpm.App/Mpm.App.csproj"
DIST="$ROOT/dist"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# publish <rid> <outdir>: self-contained single-file build of the GUI.
publish() {
  dotnet publish "$PROJECT" -c Release -r "$1" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none -o "$2"
  find "$2" -name '*.pdb' -delete   # native debug symbols shipped by SkiaSharp/HarfBuzz
}
