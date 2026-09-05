#!/usr/bin/env python3
"""Package the StreamReady plugin zip and print MD5 checksum."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from xml.etree import ElementTree


GUID = "9d2e8c4a-1f6b-4a73-b8e0-5c9f3a7d2e11"


def read_version(csproj: Path) -> str:
    tree = ElementTree.parse(csproj)
    version = tree.findtext(".//{*}AssemblyVersion")
    if not version:
        raise SystemExit("AssemblyVersion not found in csproj")
    return version.strip()


def md5_file(path: Path) -> str:
    digest = hashlib.md5()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dll", required=True)
    parser.add_argument("--thumb", required=True)
    parser.add_argument("--csproj", required=True)
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--version", default="")
    parser.add_argument("--github-output", default=os.environ.get("GITHUB_OUTPUT", ""))
    args = parser.parse_args()

    version = args.version or read_version(Path(args.csproj))
    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    zip_path = output_dir / f"jellyfin-plugin-streamready_{version}.zip"

    meta = {
        "guid": GUID,
        "name": "StreamReady",
        "description": "Pre-encode oversized or incompatible movies and episodes so clients can Direct Play.",
        "overview": "Pre-encode oversized or incompatible movies and episodes so clients can Direct Play.",
        "owner": "ThuGie",
        "category": "General",
        "version": version,
        "changelog": "See GitHub Releases.",
        "targetAbi": "10.11.0.0",
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "imagePath": "thumb.png",
    }

    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.write(args.dll, "Jellyfin.Plugin.StreamReady.dll")
        archive.write(args.thumb, "thumb.png")
        archive.writestr("meta.json", json.dumps(meta, indent=2))

    checksum = md5_file(zip_path)
    print(f"zip={zip_path}")
    print(f"version={version}")
    print(f"checksum={checksum}")

    if args.github_output:
        with open(args.github_output, "a", encoding="utf-8") as handle:
            handle.write(f"zip={zip_path}\n")
            handle.write(f"version={version}\n")
            handle.write(f"checksum={checksum}\n")
            handle.write(f"zip_name={zip_path.name}\n")


if __name__ == "__main__":
    main()
