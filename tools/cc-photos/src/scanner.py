"""Directory scanner for finding and processing images."""

import json
import os
import sqlite3
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Callable, Iterator, List, Optional, Set

from rich.progress import Progress, TaskID

from . import database as db
from .hasher import compute_sha256
from .metadata import extract_metadata
from .screenshot import detect_screenshot


# Supported image extensions
IMAGE_EXTENSIONS: Set[str] = {
    ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif"
}


@dataclass
class ScanResult:
    """Result of scanning a source."""
    source_label: str
    files_found: int
    files_added: int
    files_updated: int
    files_removed: int
    errors: List[str]


def is_image_file(path: Path) -> bool:
    """Check if a file is a supported image format."""
    return path.suffix.lower() in IMAGE_EXTENSIONS


# Directories to always skip (in addition to exclusions)
SKIP_DIRS: Set[str] = {
    'node_modules', '__pycache__', '.git', 'venv', '.venv',
    '$recycle.bin', 'system volume information',
}


def find_images(directory: Path, check_exclusions: bool = True) -> Iterator[Path]:
    """Recursively find all image files in a directory.

    Args:
        directory: Root directory to scan
        check_exclusions: Whether to check database exclusions

    Yields:
        Path objects for each image file found
    """
    for root, dirs, files in os.walk(directory):
        # Skip excluded and system directories
        dirs[:] = [d for d in dirs if d.lower() not in SKIP_DIRS and not d.startswith('.')]

        # Check if current directory is excluded
        if check_exclusions and db.is_excluded(root):
            dirs.clear()  # Don't descend into excluded directories
            continue

        for filename in files:
            file_path = Path(root) / filename
            if is_image_file(file_path):
                yield file_path


def scan_source(
    source: dict,
    progress: Optional[Progress] = None,
    task_id: Optional[TaskID] = None,
    on_file: Optional[Callable[[Path], None]] = None,
) -> ScanResult:
    """Scan a source directory and update the database.

    Args:
        source: Source dict from database with id, path, label, category
        progress: Optional Rich progress instance
        task_id: Optional task ID for progress updates
        on_file: Optional callback called for each file processed

    Returns:
        ScanResult with statistics
    """
    result = ScanResult(
        source_label=source['label'],
        files_found=0,
        files_added=0,
        files_updated=0,
        files_removed=0,
        errors=[],
    )

    source_path = Path(source['path'])
    source_id = source['id']
    category = source['category']

    if not source_path.exists():
        result.errors.append(f"Source path does not exist: {source_path}")
        return result

    # Get existing paths for this source to detect removed files
    existing_paths = set(db.get_photos_by_source(source_id))
    found_paths: Set[str] = set()

    # Find all images
    images = list(find_images(source_path))
    result.files_found = len(images)

    if progress and task_id is not None:
        progress.update(task_id, total=len(images))

    for image_path in images:
        try:
            path_str = str(image_path)
            found_paths.add(path_str)

            if on_file:
                on_file(image_path)

            # Check if file already exists in database
            existing = db.get_photo_by_path(path_str)

            # Get file stats
            stat = image_path.stat()
            file_modified = datetime.fromtimestamp(stat.st_mtime)
            file_modified_str = file_modified.isoformat()

            # Skip if file hasn't changed
            if existing and existing.get('file_modified_at'):
                existing_mtime = existing['file_modified_at']
                if isinstance(existing_mtime, str):
                    existing_mtime = datetime.fromisoformat(existing_mtime)
                if existing_mtime >= file_modified:
                    if progress and task_id is not None:
                        progress.advance(task_id)
                    continue

            # Extract metadata
            metadata = extract_metadata(image_path)

            # Detect screenshot
            screenshot_result = detect_screenshot(image_path, metadata)

            # Compute hash
            file_hash = compute_sha256(image_path)

            if existing:
                # Update existing record
                db.update_photo(
                    photo_id=existing['id'],
                    sha256_hash=file_hash,
                    is_screenshot=screenshot_result.is_screenshot,
                    screenshot_confidence=screenshot_result.confidence,
                    file_modified_at=file_modified_str,
                )
                photo_id = existing['id']
                result.files_updated += 1
            else:
                # Add new record
                photo_id = db.add_photo(
                    source_id=source_id,
                    file_path=path_str,
                    file_name=image_path.name,
                    category=category,
                    file_size=stat.st_size,
                    sha256_hash=file_hash,
                    is_screenshot=screenshot_result.is_screenshot,
                    screenshot_confidence=screenshot_result.confidence,
                    file_modified_at=file_modified_str,
                )
                result.files_added += 1

            # Store metadata
            raw_exif_json = json.dumps(metadata.raw_exif) if metadata.raw_exif else None
            date_taken_str = metadata.date_taken.isoformat() if metadata.date_taken else None

            db.add_metadata(
                photo_id=photo_id,
                width=metadata.width,
                height=metadata.height,
                date_taken=date_taken_str,
                camera_make=metadata.camera_make,
                camera_model=metadata.camera_model,
                gps_lat=metadata.gps_lat,
                gps_lon=metadata.gps_lon,
                orientation=metadata.orientation,
                raw_exif=raw_exif_json,
            )

        except (OSError, ValueError, sqlite3.Error) as e:
            result.errors.append(f"{image_path}: {e}")

        if progress and task_id is not None:
            progress.advance(task_id)

    # Remove files that no longer exist
    missing_paths = existing_paths - found_paths
    if missing_paths:
        result.files_removed = db.delete_missing_photos(source_id, list(found_paths))

    return result
