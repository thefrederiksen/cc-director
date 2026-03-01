"""Backup all cc-director storage locations before migration.

Creates a timestamped .zip file under a Backups directory.
Copies ALL old and new locations that have data into the zip.
Does NOT delete anything.

Override backup location with CC_BACKUP_DIR environment variable.

Usage:
    python scripts/backup-before-migration.py
"""

import os
import sys
import zipfile
from datetime import datetime
from pathlib import Path


def _local() -> str:
    return os.environ.get("LOCALAPPDATA", "")


def _home() -> Path:
    return Path.home()


def _docs() -> str:
    return os.path.join(os.environ.get("USERPROFILE", str(_home())), "Documents")


def get_backup_sources():
    """Return list of (label, source_path) tuples for all known storage locations."""
    local = _local()
    home = _home()
    docs = _docs()

    sources = []

    # Old director configs
    if local:
        sources.append(("old-CcDirector-localappdata", os.path.join(local, "CcDirector")))
        sources.append(("old-cc-myvault", os.path.join(local, "cc-myvault")))
        sources.append(("new-cc-director", os.path.join(local, "cc-director")))
        sources.append(("old-cc-browser", os.path.join(local, "cc-browser")))
        sources.append(("old-cc-tools-data", os.path.join(local, "cc-tools", "data")))

    # Documents
    sources.append(("old-Documents-CcDirector", os.path.join(docs, "CcDirector")))

    # Home-based configs
    sources.append(("old-dot-cc-reddit", str(home / ".cc-reddit")))
    sources.append(("old-dot-cc-linkedin", str(home / ".cc-linkedin")))
    sources.append(("old-dot-cc-vault", str(home / ".cc-vault")))
    sources.append(("old-dot-cc_tools", str(home / ".cc_tools")))
    sources.append(("old-dot-cc-tools", str(home / ".cc-tools")))

    return sources


def format_size(num_bytes):
    """Format bytes as human-readable string."""
    if num_bytes < 1024:
        return f"{num_bytes} B"
    elif num_bytes < 1024 * 1024:
        return f"{num_bytes / 1024:.1f} KB"
    elif num_bytes < 1024 * 1024 * 1024:
        return f"{num_bytes / (1024 * 1024):.1f} MB"
    else:
        return f"{num_bytes / (1024 * 1024 * 1024):.2f} GB"


MIN_ZIP_DATE = (1980, 1, 1, 0, 0, 0)


def _safe_write(zf, full_path, arcname):
    """Write a file to the zip, clamping pre-1980 timestamps to 1980-01-01."""
    mtime = os.path.getmtime(full_path)
    local_time = datetime.fromtimestamp(mtime).timetuple()[:6]
    if local_time < MIN_ZIP_DATE:
        info = zipfile.ZipInfo(arcname, date_time=MIN_ZIP_DATE)
        info.compress_type = zipfile.ZIP_DEFLATED
        with open(full_path, "rb") as fh:
            zf.writestr(info, fh.read())
    else:
        zf.write(full_path, arcname)


def add_source_to_zip(zf, label, src_path):
    """Add a source directory or file to the zip under label/. Returns (files_added, bytes_added)."""
    files_added = 0
    bytes_added = 0

    if os.path.isfile(src_path):
        arcname = f"{label}/{os.path.basename(src_path)}"
        _safe_write(zf, src_path, arcname)
        size = os.path.getsize(src_path)
        files_added += 1
        bytes_added += size
        return files_added, bytes_added

    for root, dirs, files in os.walk(src_path):
        for f in files:
            full_path = os.path.join(root, f)
            rel = os.path.relpath(full_path, src_path)
            arcname = f"{label}/{rel}"
            _safe_write(zf, full_path, arcname)
            size = os.path.getsize(full_path)
            files_added += 1
            bytes_added += size

    return files_added, bytes_added


def main():
    timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    backup_base = os.environ.get("CC_BACKUP_DIR", str(Path.home() / "Backups"))
    zip_path = Path(backup_base) / f"cc-director-migration-{timestamp}.zip"

    print(f"[INFO] Backup destination: {zip_path}")
    print()

    sources = get_backup_sources()

    # Check what exists
    found = []
    for label, src_path in sources:
        if os.path.exists(src_path):
            found.append((label, src_path))

    if not found:
        print("[INFO] No storage locations found. Nothing to back up.")
        return

    print(f"[INFO] Found {len(found)} storage location(s) with data:")
    for label, src_path in found:
        print(f"  - [{label}] {src_path}")
    print()

    # Create backup directory
    zip_path.parent.mkdir(parents=True, exist_ok=True)

    total_files = 0
    total_size = 0

    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for label, src_path in found:
            print(f"[BACKUP] {label}")
            print(f"         {src_path}")

            added, size = add_source_to_zip(zf, label, src_path)
            total_files += added
            total_size += size

            print(f"         {added} files ({format_size(size)})")
            print()

    zip_size = os.path.getsize(zip_path)

    print("=" * 60)
    print(f"[DONE] Backup complete")
    print(f"  Zip file:       {zip_path}")
    print(f"  Files in zip:   {total_files}")
    print(f"  Original size:  {format_size(total_size)}")
    print(f"  Zip size:       {format_size(zip_size)}")
    print("=" * 60)


if __name__ == "__main__":
    main()
