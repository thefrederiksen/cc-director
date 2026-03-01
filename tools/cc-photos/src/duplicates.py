"""Duplicate detection and cleanup for photos."""

import os
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Tuple

from . import database as db


@dataclass
class DuplicateGroup:
    """A group of duplicate photos."""
    hash: str
    photos: List[Dict]
    keep: Dict  # The one to keep (highest priority source)
    duplicates: List[Dict]  # The ones to remove
    wasted_bytes: int


def find_duplicate_groups() -> List[DuplicateGroup]:
    """Find groups of duplicate photos.

    Returns:
        List of DuplicateGroup objects
    """
    groups = []

    for photos in db.get_duplicate_groups():
        if len(photos) < 2:
            continue

        # Photos are already sorted by source priority from the vault query
        keep = photos[0]
        duplicates = photos[1:]

        # Calculate wasted space
        wasted = sum(p.get('file_size', 0) or 0 for p in duplicates)

        groups.append(DuplicateGroup(
            hash=photos[0].get('sha256_hash', ''),
            photos=photos,
            keep=keep,
            duplicates=duplicates,
            wasted_bytes=wasted,
        ))

    return groups


def get_duplicate_stats(groups: List[DuplicateGroup]) -> Dict:
    """Get statistics about duplicate groups.

    Args:
        groups: List of DuplicateGroup objects

    Returns:
        Dict with statistics
    """
    total_wasted = sum(g.wasted_bytes for g in groups)
    total_duplicates = sum(len(g.duplicates) for g in groups)

    return {
        "groups": len(groups),
        "duplicate_files": total_duplicates,
        "wasted_bytes": total_wasted,
        "wasted_mb": total_wasted / (1024 * 1024),
    }


def delete_duplicates(
    groups: List[DuplicateGroup],
    dry_run: bool = False,
) -> Tuple[int, int, List[str]]:
    """Delete duplicate files, keeping the highest priority copy.

    Args:
        groups: List of DuplicateGroup objects
        dry_run: If True, don't actually delete files

    Returns:
        Tuple of (files_deleted, bytes_freed, errors)
    """
    files_deleted = 0
    bytes_freed = 0
    errors = []

    for group in groups:
        for dup in group.duplicates:
            file_path = Path(dup['file_path'])

            try:
                if not dry_run:
                    # Delete the file
                    if file_path.exists():
                        os.remove(file_path)

                    # Remove from database
                    db.delete_photo(dup['id'])

                files_deleted += 1
                bytes_freed += dup.get('file_size', 0) or 0

            except OSError as e:
                errors.append(f"Failed to delete {file_path}: {e}")

    return files_deleted, bytes_freed, errors


def format_file_size(size_bytes: int) -> str:
    """Format file size in human-readable format."""
    if size_bytes < 1024:
        return f"{size_bytes} B"
    elif size_bytes < 1024 * 1024:
        return f"{size_bytes / 1024:.1f} KB"
    elif size_bytes < 1024 * 1024 * 1024:
        return f"{size_bytes / (1024 * 1024):.1f} MB"
    else:
        return f"{size_bytes / (1024 * 1024 * 1024):.2f} GB"
