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
  # AGPL text for the app, plus attribution for the WoWDBDefs layouts embedded in it (CC BY-SA 4.0).
  cp "$ROOT/LICENSE" "$2/LICENSE.txt"
  cp "$ROOT/data/wowdbdefs/LICENSE.md" "$2/WoWDBDefs-LICENSE.md"
  cp "$ROOT/data/wowdbdefs/ATTRIBUTION.md" "$2/WoWDBDefs-ATTRIBUTION.md"
}
