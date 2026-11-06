#!/usr/bin/env bash
# Compile le plugin et prépare artifacts/dl-helper_<version>/ (+ .zip) prêt à copier dans le dossier plugins de Jellyfin.
set -euo pipefail
cd "$(dirname "$0")"
# Version : 1er argument (ex. 1.0.1.0, utilisé par la CI depuis le tag), sinon celle du .csproj.
VERSION=${1:-$(grep -oP '(?<=<AssemblyVersion>)[^<]+' Jellyfin.Plugin.DlHelper/Jellyfin.Plugin.DlHelper.csproj)}
ABI=$(grep -oP '(?<=<JellyfinVersion>)[^<]+' Jellyfin.Plugin.DlHelper/Jellyfin.Plugin.DlHelper.csproj).0
OUT="artifacts/dl-helper_${VERSION}"
dotnet publish Jellyfin.Plugin.DlHelper -c Release -o artifacts/publish --nologo -v q -p:AssemblyVersion="$VERSION" -p:FileVersion="$VERSION"
rm -rf "$OUT" && mkdir -p "$OUT"
cp artifacts/publish/Jellyfin.Plugin.DlHelper.dll "$OUT/"
cat > "$OUT/meta.json" <<JSON
{
  "category": "General",
  "changelog": "",
  "description": "Range les nouveaux téléchargements Syncthing dans Anime, Musique, Films ou Séries, depuis l'interface web.",
  "guid": "252483f4-bd35-44be-ba5e-49e971a89efe",
  "name": "dl-helper",
  "overview": "Ranger les téléchargements en un clic",
  "owner": "fauwzk",
  "targetAbi": "${ABI}",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "${VERSION}",
  "status": "Active",
  "autoUpdate": false,
  "imagePath": "",
  "assemblies": ["Jellyfin.Plugin.DlHelper.dll"]
}
JSON
rm -f "$OUT.zip"
(cd "$OUT" && python3 -c "import zipfile,os; z=zipfile.ZipFile('../$(basename "$OUT").zip','w',zipfile.ZIP_DEFLATED); [z.write(f) for f in os.listdir('.')]" 2>/dev/null \
  || python -c "import zipfile,os; z=zipfile.ZipFile('../$(basename "$OUT").zip','w',zipfile.ZIP_DEFLATED); [z.write(f) for f in os.listdir('.')]")
echo "✅ $OUT (+ .zip)"
