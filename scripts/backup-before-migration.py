"""Backup all cc-director storage locations before migration.

Creates a timestamped backup under a Backups directory.
Copies ALL old and new locations that have data.
Does NOT delete anything.

Override backup location with CC_BACKUP_DIR environment variable.

Usage:
    python scripts/backup-before-migration.py
"""

import os
import shutil
import sys
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


def copy_tree_verified(src, dst):
    """Copy a directory tree and return (files_copied, files_failed, files_locked, total_bytes)."""
    files_copied = 0
    files_failed = 0
    files_locked = 0
    total_bytes = 0

    if os.path.isfile(src):
        # Single file
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(src, dst)
        src_size = os.path.getsize(src)
        dst_size = os.path.getsize(dst)
        if src_size == dst_size:
            return 1, 0, 0, src_size
        else:
            return 0, 1, 0, 0

    if not os.path.isdir(src):
        return 0, 0, 0, 0

    for root, dirs, files in os.walk(src):
        rel = os.path.relpath(root, src)
        dest_root = os.path.join(dst, rel) if rel != "." else dst
        os.makedirs(dest_root, exist_ok=True)

        for f in files:
            src_file = os.path.join(root, f)
            dst_file = os.path.join(dest_root, f)
            try:
                shutil.copy2(src_file, dst_file)
                src_size = os.path.getsize(src_file)
                dst_size = os.path.getsize(dst_file)
                if src_size == dst_size:
                    files_copied += 1
                    total_bytes += src_size
                else:
                    files_failed += 1
                    print(f"  [!] Size mismatch: {src_file} ({src_size}) vs {dst_file} ({dst_size})")
            except PermissionError:
                # Locked files (browser cache, lockfiles) -- skip silently
                files_locked += 1
            except Exception as e:
                files_failed += 1
                print(f"  [!] Failed to copy: {src_file} -> {e}")

    return files_copied, files_failed, files_locked, total_bytes


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


def main():
    timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    backup_base = os.environ.get("CC_BACKUP_DIR", str(Path.home() / "Backups"))
    backup_root = Path(backup_base) / f"cc-director-migration-{timestamp}"

    print(f"[INFO] Backup destination: {backup_root}")
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

    # Create backup root
    backup_root.mkdir(parents=True, exist_ok=True)

    total_files = 0
    total_failed = 0
    total_locked = 0
    total_size = 0

    for label, src_path in found:
        dst_path = backup_root / label
        print(f"[BACKUP] {src_path}")
        print(f"     ->  {dst_path}")

        copied, failed, locked, size = copy_tree_verified(src_path, str(dst_path))
        total_files += copied
        total_failed += failed
        total_locked += locked
        total_size += size

        status_parts = [f"{copied} files ({format_size(size)})"]
        if locked:
            status_parts.append(f"{locked} locked/skipped")
        if failed:
            status_parts.append(f"{failed} failed")
        print(f"         {', '.join(status_parts)}")
        print()

    print("=" * 60)
    print(f"[DONE] Backup complete")
    print(f"  Location:    {backup_root}")
    print(f"  Files:       {total_files}")
    print(f"  Total size:  {format_size(total_size)}")
    if total_locked:
        print(f"  Locked:      {total_locked} (browser cache/lockfiles -- safe to skip)")
    if total_failed:
        print(f"  FAILED:      {total_failed}")
    print("=" * 60)

    if total_failed:
        print()
        print("[WARNING] Some files failed to copy. Check output above.")
        sys.exit(1)


if __name__ == "__main__":
    main()
