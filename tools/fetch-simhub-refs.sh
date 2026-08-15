#!/usr/bin/env bash
# Extracts SimHub reference assemblies into lib/ WITHOUT installing SimHub.
# Ported from the sibling reliable-wheel-lock project's tools/fetch-simhub-refs.sh, adapted for this
# project's own lib\ (seven assemblies, not six - see the loop below).
#
# Targets SimHub 9.11.22 by default (the version this repo's own lib\ was captured from - every DLL
# in lib\ matches this release byte-for-byte). Pass a different version as $1 to target another
# release; there is no guarantee a different version's assembly layout inside the installer matches.
#
# innoextract 1.9 and innounp 1.x do NOT support Inno Setup 6.4 - innounp-2 >= 2.67 is required.
#
# Runnable on a machine with no SimHub installed: everything this script needs (the SimHub installer
# itself, and the innounp2 unpacker) is downloaded fresh into a scratch working directory and never
# touches Program Files or the Windows registry.
set -euo pipefail
VERSION="${1:-9.11.22}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$ROOT/.simhub-refs-work"
mkdir -p "$WORK" "$ROOT/lib"

curl -fsSL -o "$WORK/simhub.zip" \
  "https://github.com/SHWotever/SimHub/releases/download/$VERSION/SimHub.$VERSION.zip"
unzip -o -q "$WORK/simhub.zip" -d "$WORK"

curl -fsSL -o "$WORK/innounp2.zip" \
  "https://github.com/jrathlev/InnoUnpacker-Windows-GUI/releases/download/oi_2_2_11/innounp-2.zip"
unzip -o -q "$WORK/innounp2.zip" -d "$WORK/innounp2"

"$WORK/innounp2/innounp.exe" -x -y -d"$WORK/out" \
  "$WORK/SimHubSetup_$VERSION.exe" "{app}\\*.dll" >/dev/null

# Seven reference assemblies this plugin's csproj links against with <Private>false</Private> (see
# QAdvanceFeedback.csproj's own remarks) - System.Windows.Interactivity is required by the settings
# UI's MahApps-based controls (the sibling project's own copy of this script omits it, which is a gap
# in that script, not a difference in what its own lib\ actually needs - fixed here).
for d in SimHub.Plugins GameReaderCommon SimHub.Logging log4net Newtonsoft.Json MahApps.Metro System.Windows.Interactivity; do
  cp "$WORK/out/{app}/$d.dll" "$ROOT/lib/"
done
echo "Reference assemblies written to $ROOT/lib (SimHub $VERSION)"
