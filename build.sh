#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
LIDARR="$ROOT/ext/Lidarr"
PROJECT="$ROOT/src/Lidarr.Plugin.YandexMusic.Metadata/Lidarr.Plugin.YandexMusic.Metadata.csproj"
DIST="$ROOT/dist"
PUBLISH="$DIST/YandexMusicMetadata"

if [ ! -f "$LIDARR/src/NzbDrone.Core/Lidarr.Core.csproj" ]; then
  rm -rf "$LIDARR"
  git clone --depth 1 --branch plugins https://github.com/Lidarr/Lidarr.git "$LIDARR"
fi

rm -rf "$DIST"
mkdir -p "$PUBLISH"

dotnet publish "$PROJECT" -c Release -o "$PUBLISH" \
  -p:NuGetAudit=false \
  -p:AssemblyVersion=1.0.0 \
  -p:FileVersion=1.0.0 \
  -p:Deterministic=true \
  -p:EnableAnalyzers=false \
  -p:TreatWarningsAsErrors=false

rm -f "$PUBLISH/Lidarr.Core.dll" "$PUBLISH/Lidarr.Common.dll" "$PUBLISH/NLog.dll"
(cd "$PUBLISH" && zip -qr "$DIST/Lidarr.Plugin.YandexMusic.Metadata-v0.2.7.net8.0.zip" .)
