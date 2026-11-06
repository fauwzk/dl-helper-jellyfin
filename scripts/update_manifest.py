#!/usr/bin/env python3
"""Ajoute (ou remplace) une version dans manifest.json, le dépôt de plugins lu par Jellyfin.

Usage : update_manifest.py <version> <zip> <url du zip> <targetAbi> [changelog]
"""
import datetime
import hashlib
import json
import pathlib
import sys

version, zip_path, url, abi = sys.argv[1:5]
changelog = sys.argv[5] if len(sys.argv) > 5 else ""
manifest_path = pathlib.Path(__file__).resolve().parent.parent / "manifest.json"

manifest = json.loads(manifest_path.read_text(encoding="utf-8") or "[]")
plugin = next((p for p in manifest if p["guid"] == "252483f4-bd35-44be-ba5e-49e971a89efe"), None)
if plugin is None:
    plugin = {
        "guid": "252483f4-bd35-44be-ba5e-49e971a89efe",
        "name": "dl-helper",
        "overview": "Ranger les téléchargements Syncthing en un clic",
        "description": "Surveille un dossier de téléchargements (Syncthing) et propose, pour chaque nouveau "
                       "téléchargement terminé, de le copier ou de le déplacer dans Anime, Musique, Films ou Séries, "
                       "depuis l'interface web de Jellyfin. Notifications ntfy / Discord.",
        "owner": "fauwzk",
        "category": "General",
        "versions": [],
    }
    manifest.append(plugin)

plugin["versions"] = [v for v in plugin["versions"] if v["version"] != version]
plugin["versions"].insert(0, {
    "version": version,
    "changelog": changelog,
    "targetAbi": abi,
    "sourceUrl": url,
    "checksum": hashlib.md5(pathlib.Path(zip_path).read_bytes()).hexdigest(),
    "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
})
manifest_path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print(f"manifest.json : version {version} ajoutée")
