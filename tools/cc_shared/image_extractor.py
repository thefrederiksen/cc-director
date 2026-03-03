"""Shared image extraction utilities for to-markdown conversions.

Provides consistent image saving and markdown reference generation
across all cc-* document tools.
"""

import re
from dataclasses import dataclass, field
from pathlib import Path


@dataclass
class ExtractedImage:
    """An image extracted from a document during to-markdown conversion."""

    data: bytes
    extension: str
    original_name: str = ""
    alt_text: str = ""


def sanitize_filename(name: str) -> str:
    """Sanitize a filename by removing unsafe characters.

    Args:
        name: Original filename or label.

    Returns:
        Safe filename string with only alphanumeric, dash, underscore, and dot.
    """
    # Replace spaces with underscores
    name = name.replace(" ", "_")
    # Remove anything that isn't alphanumeric, dash, underscore, or dot
    name = re.sub(r"[^\w\-.]", "", name)
    # Collapse multiple underscores
    name = re.sub(r"_+", "_", name)
    return name.strip("_.")


def save_extracted_images(
    images: list[ExtractedImage],
    output_md_path: Path,
) -> dict[str, str]:
    """Save extracted images to a sibling directory and return markdown paths.

    Creates a directory named ``{stem}_images/`` next to the output markdown file.
    Each image is saved with a sequential filename (``image_001.png``, etc.) unless
    the image already has a meaningful original name.

    Args:
        images: List of extracted images to save.
        output_md_path: Path to the output ``.md`` file (used to derive the
            image directory name).

    Returns:
        Mapping of ``original_name`` -> relative markdown path for use in
        ``![alt](path)`` references.  If an image has no ``original_name``,
        the generated sequential name is used as key.
    """
    if not images:
        return {}

    stem = output_md_path.stem
    images_dir = output_md_path.parent / f"{stem}_images"
    images_dir.mkdir(parents=True, exist_ok=True)

    path_map: dict[str, str] = {}

    for idx, img in enumerate(images, start=1):
        ext = img.extension.lstrip(".")
        if not ext:
            ext = "png"

        # Build filename
        if img.original_name:
            safe_name = sanitize_filename(Path(img.original_name).stem)
            if not safe_name:
                safe_name = f"image_{idx:03d}"
            filename = f"{safe_name}.{ext}"
        else:
            filename = f"image_{idx:03d}.{ext}"

        # Handle duplicates by appending index
        dest = images_dir / filename
        if dest.exists():
            filename = f"image_{idx:03d}_{sanitize_filename(Path(img.original_name).stem)}.{ext}" if img.original_name else f"image_{idx:03d}.{ext}"
            dest = images_dir / filename

        dest.write_bytes(img.data)

        # Store mapping using original_name as key (or generated name)
        key = img.original_name if img.original_name else filename
        relative_path = f"{stem}_images/{filename}"
        path_map[key] = relative_path

    return path_map
