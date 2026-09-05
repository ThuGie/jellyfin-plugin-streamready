#!/usr/bin/env python3
"""Upsert a StreamReady version into the Jellyfin catalog manifest.json."""

from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path


GUID = "9d2e8c4a-1f6b-4a73-b8e0-5c9f3a7d2e11"
IMAGE_URL = "https://raw.githubusercontent.com/ThuGie/jellyfin-plugin-streamready/main/images/logo.png"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--checksum", required=True)
    parser.add_argument("--source-url", required=True)
    parser.add_argument("--changelog", default="See GitHub Releases.")
    args = parser.parse_args()

    path = Path(args.manifest)
    catalog = json.loads(path.read_text(encoding="utf-8")) if path.exists() else []
    if not isinstance(catalog, list):
        catalog = []

    plugin = next((item for item in catalog if str(item.get("guid", "")).lower() == GUID), None)
    if plugin is None:
        plugin = {
            "guid": GUID,
            "name": "StreamReady",
            "description": "Pre-encode oversized or incompatible movies and episodes so clients can Direct Play. Manual review list or Auto Direct Pre-Transcode.",
            "overview": "Pre-encode oversized or incompatible movies and episodes so clients can Direct Play.",
            "owner": "ThuGie",
            "category": "General",
            "imageUrl": IMAGE_URL,
            "versions": [],
        }
        catalog.append(plugin)

    plugin["imageUrl"] = IMAGE_URL
    versions = plugin.setdefault("versions", [])
    entry = {
        "version": args.version,
        "changelog": args.changelog.strip() or "See GitHub Releases.",
        "targetAbi": "10.11.0.0",
        "sourceUrl": args.source_url,
        "checksum": args.checksum.lower(),
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }

    versions[:] = [item for item in versions if item.get("version") != args.version]
    versions.insert(0, entry)
    path.write_text(json.dumps(catalog, indent=2) + "\n", encoding="utf-8")
    print(f"Updated {path} with {args.version} checksum {args.checksum}")


if __name__ == "__main__":
    main()
