#!/usr/bin/env python3
"""Build the Zorofin Jellyfin plugin and add a release to a plugin repository manifest.

Usage:
  python3 tools/package-zorofin-plugin.py --version 0.1.0.0 --changelog "..." \
      --manifest manifest.json [--repo Zoraz01/jellyfin-plugin-zorofin] [--out dist]

Writes <out>/zorofin_<version>.zip and inserts (or replaces) that version in the
manifest with its MD5 checksum, targetAbi and GitHub release download URL.
"""
import argparse
import datetime
import hashlib
import json
import re
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PLUGIN_DIR = ROOT / "server" / "jellyfin-plugin-zorofin"
PROJECT = PLUGIN_DIR / "src" / "Jellyfin.Plugin.Zorofin" / "Jellyfin.Plugin.Zorofin.csproj"
TEMPLATE = ROOT / "docs" / "community" / "plugin-release" / "manifest.template.json"
GUID = "e49272bf-d1f8-4d83-8cd4-7b3c7f2a1fce"
DLL = "Jellyfin.Plugin.Zorofin.dll"


def target_abi(csproj: str) -> str:
    match = re.search(r'Include="Jellyfin\.Controller"\s+Version="([0-9.]+)"', csproj)
    if not match:
        sys.exit("Could not find the Jellyfin.Controller version in the .csproj")
    parts = match.group(1).split(".")
    return ".".join((parts + ["0"] * 4)[:4])


def build(version: str, workdir: Path) -> Path:
    subprocess.run(
        ["dotnet", "publish", str(PROJECT), "-c", "Release", "-o", str(workdir),
         f"-p:AssemblyVersion={version}", f"-p:FileVersion={version}"],
        check=True,
    )
    dll = workdir / DLL
    if not dll.exists():
        sys.exit(f"Build finished but {DLL} is missing")
    return dll


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--version", required=True, help="four-part version, e.g. 0.1.0.0")
    parser.add_argument("--changelog", required=True)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--repo", default="Zoraz01/jellyfin-plugin-zorofin")
    parser.add_argument("--out", default=ROOT / "dist", type=Path)
    args = parser.parse_args()

    if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", args.version):
        sys.exit("--version must have four parts, e.g. 0.1.0.0")

    abi = target_abi(PROJECT.read_text())
    args.out.mkdir(parents=True, exist_ok=True)
    zip_path = args.out / f"zorofin_{args.version}.zip"

    with tempfile.TemporaryDirectory() as tmp:
        dll = build(args.version, Path(tmp))
        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as archive:
            archive.write(dll, DLL)
            archive.write(PLUGIN_DIR / "LICENSE", "LICENSE")

    checksum = hashlib.md5(zip_path.read_bytes()).hexdigest()
    manifest_path: Path = args.manifest
    source = manifest_path if manifest_path.exists() else TEMPLATE
    manifest = json.loads(source.read_text())
    plugin = next((p for p in manifest if p.get("guid") == GUID), None)
    if plugin is None:
        sys.exit(f"{source} has no plugin with guid {GUID}")

    entry = {
        "version": args.version,
        "changelog": args.changelog,
        "targetAbi": abi,
        "sourceUrl": f"https://github.com/{args.repo}/releases/download/v{args.version}/{zip_path.name}",
        "checksum": checksum,
        "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }
    versions = [v for v in plugin.get("versions", []) if v.get("version") != args.version]
    plugin["versions"] = [entry] + versions
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")

    print(f"zip:       {zip_path}")
    print(f"checksum:  {checksum}")
    print(f"targetAbi: {abi}")
    print(f"manifest:  {manifest_path}")


if __name__ == "__main__":
    main()
