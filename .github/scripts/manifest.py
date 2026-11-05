#!/usr/bin/env python3
"""Prepara el meta.json del paquete y añade la versión al manifest del repositorio de plugins.

El changelog llega en la variable de entorno CHANGELOG.

Uso:
  manifest.py meta <version> <salida/meta.json>
  manifest.py manifest <version> <zip> <url del zip> <manifest.json>
"""
import datetime
import hashlib
import json
import os
import sys

GUID = "e2a567a4-0c6d-4789-9a72-f672d24268b8"  # Plugin.PluginGuid
NAME = "Jellyfind"
TARGET_ABI = "12.2.0.0"  # versión de Jellyfin.Controller del .csproj
DESCRIPTION = "Busca y pide series y películas desde Jellyfin con Sonarr, Radarr, Prowlarr y qBittorrent"
OVERVIEW = "Pide series y películas que no están en tu biblioteca"


def now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.0000000Z")


def changelog():
    return os.environ.get("CHANGELOG", "").strip()


def write_json(path, data):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.write("\n")


def write_meta(version, out):
    write_json(out, {
        "category": "General",
        "changelog": changelog(),
        "description": DESCRIPTION,
        "guid": GUID,
        "name": NAME,
        "overview": OVERVIEW,
        "owner": "Arata12",
        "targetAbi": TARGET_ABI,
        "timestamp": now(),
        "version": version,
        "status": "Active",
        "autoUpdate": False,
        "assemblies": [],
    })


def update_manifest(version, zip_path, url, manifest_path):
    with open(zip_path, "rb") as f:
        checksum = hashlib.md5(f.read()).hexdigest()
    try:
        with open(manifest_path, encoding="utf-8") as f:
            manifest = json.load(f)
    except FileNotFoundError:
        manifest = []

    old = next((p for p in manifest if p.get("guid") == GUID), {})
    versions = [v for v in old.get("versions", []) if v.get("version") != version]
    versions.append({
        "version": version,
        "changelog": changelog(),
        "targetAbi": TARGET_ABI,
        "sourceUrl": url,
        "checksum": checksum,
        "timestamp": now(),
    })
    versions.sort(key=lambda v: tuple(int(n) for n in v["version"].split(".")), reverse=True)
    plugin = {
        "guid": GUID,
        "name": NAME,
        "description": DESCRIPTION,
        "overview": OVERVIEW,
        "owner": "Arata12",
        "category": "General",
        "versions": versions,
    }
    manifest = [plugin if p is old else p for p in manifest] if old else manifest + [plugin]
    write_json(manifest_path, manifest)


if __name__ == "__main__":
    if len(sys.argv) == 4 and sys.argv[1] == "meta":
        write_meta(sys.argv[2], sys.argv[3])
    elif len(sys.argv) == 6 and sys.argv[1] == "manifest":
        update_manifest(*sys.argv[2:])
    else:
        sys.exit(__doc__)
