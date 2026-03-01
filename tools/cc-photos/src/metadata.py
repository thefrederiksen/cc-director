"""EXIF metadata extraction for images.

Supports JPEG, PNG, HEIC, HEIF, and other common image formats.
"""

import logging
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, Optional, Tuple

from PIL import Image
from PIL.ExifTags import TAGS, GPSTAGS

logger = logging.getLogger(__name__)


@dataclass
class ImageMetadata:
    """Extracted image metadata."""
    width: int = 0
    height: int = 0
    format: Optional[str] = None
    date_taken: Optional[datetime] = None
    camera_make: Optional[str] = None
    camera_model: Optional[str] = None
    gps_lat: Optional[float] = None
    gps_lon: Optional[float] = None
    orientation: Optional[int] = None
    raw_exif: Dict[str, Any] = field(default_factory=dict)


def _decode_exif_value(value: Any) -> Any:
    """Decode EXIF value to a JSON-serializable type."""
    if isinstance(value, bytes):
        try:
            return value.decode("utf-8", errors="ignore")
        except (UnicodeDecodeError, AttributeError):
            return str(value)
    elif isinstance(value, tuple):
        return [_decode_exif_value(v) for v in value]
    elif hasattr(value, "numerator") and hasattr(value, "denominator"):
        # IFDRational
        if value.denominator == 0:
            return 0
        return float(value.numerator) / float(value.denominator)
    else:
        return value


def _parse_exif_datetime(dt_string: str) -> Optional[datetime]:
    """Parse EXIF datetime string to datetime object."""
    if not dt_string:
        return None

    formats = [
        "%Y:%m:%d %H:%M:%S",
        "%Y-%m-%d %H:%M:%S",
        "%Y:%m:%d",
        "%Y-%m-%d",
    ]

    for fmt in formats:
        try:
            return datetime.strptime(dt_string.strip(), fmt)
        except ValueError:
            continue

    return None


def _convert_gps_to_decimal(gps_coords: Tuple, gps_ref: str) -> Optional[float]:
    """Convert GPS coordinates from degrees/minutes/seconds to decimal."""
    if not gps_coords or len(gps_coords) != 3:
        return None

    try:
        degrees = float(gps_coords[0])
        minutes = float(gps_coords[1])
        seconds = float(gps_coords[2])

        decimal = degrees + (minutes / 60.0) + (seconds / 3600.0)

        if gps_ref in ("S", "W"):
            decimal = -decimal

        return decimal
    except (TypeError, ValueError, ZeroDivisionError):
        return None


def _extract_gps(exif_data: Dict[str, Any]) -> Tuple[Optional[float], Optional[float]]:
    """Extract GPS coordinates from EXIF data."""
    gps_info = exif_data.get("GPSInfo")
    if not gps_info:
        return None, None

    # GPSInfo is a dict with numeric keys, decode to tag names
    gps_decoded = {}
    for key, value in gps_info.items():
        tag_name = GPSTAGS.get(key, key)
        gps_decoded[tag_name] = value

    lat = None
    lon = None

    if "GPSLatitude" in gps_decoded and "GPSLatitudeRef" in gps_decoded:
        lat = _convert_gps_to_decimal(
            gps_decoded["GPSLatitude"],
            gps_decoded["GPSLatitudeRef"],
        )

    if "GPSLongitude" in gps_decoded and "GPSLongitudeRef" in gps_decoded:
        lon = _convert_gps_to_decimal(
            gps_decoded["GPSLongitude"],
            gps_decoded["GPSLongitudeRef"],
        )

    return lat, lon


def extract_metadata(image_path: Path) -> ImageMetadata:
    """Extract metadata from an image file.

    Args:
        image_path: Path to image file

    Returns:
        ImageMetadata object with extracted data
    """
    metadata = ImageMetadata()

    # Try to load HEIC/HEIF support
    suffix = image_path.suffix.lower()
    if suffix in (".heic", ".heif"):
        try:
            from pillow_heif import register_heif_opener
            register_heif_opener()
        except ImportError:
            logger.debug("pillow_heif not installed, HEIC/HEIF support unavailable")

    try:
        with Image.open(image_path) as img:
            metadata.width = img.width
            metadata.height = img.height
            metadata.format = img.format

            # Get EXIF data
            exif = img.getexif()
            if exif:
                exif_data = {}

                # Decode main EXIF tags
                for tag_id, value in exif.items():
                    tag_name = TAGS.get(tag_id, tag_id)
                    decoded_value = _decode_exif_value(value)
                    exif_data[str(tag_name)] = decoded_value

                # Get IFD (sub-IFDs like EXIF IFD)
                for ifd_id in exif.get_ifd(0x8769) if 0x8769 in exif else {}:
                    tag_name = TAGS.get(ifd_id, ifd_id)
                    value = exif.get_ifd(0x8769).get(ifd_id)
                    if value is not None:
                        exif_data[str(tag_name)] = _decode_exif_value(value)

                # Store raw EXIF (filtered for JSON serialization)
                metadata.raw_exif = exif_data

                # Extract specific fields
                metadata.camera_make = exif_data.get("Make")
                metadata.camera_model = exif_data.get("Model")
                metadata.orientation = exif_data.get("Orientation")

                # Date taken - try multiple fields
                date_str = (
                    exif_data.get("DateTimeOriginal")
                    or exif_data.get("DateTime")
                    or exif_data.get("DateTimeDigitized")
                )
                if date_str:
                    metadata.date_taken = _parse_exif_datetime(str(date_str))

                # GPS coordinates
                metadata.gps_lat, metadata.gps_lon = _extract_gps(exif_data)

    except (OSError, IOError) as e:
        # Return partial metadata if file read fails
        logger.debug("Failed to extract metadata from %s: %s", image_path, e)
    except (KeyError, TypeError, ValueError) as e:
        # Return partial metadata if EXIF parsing fails
        logger.debug("Failed to parse EXIF data from %s: %s", image_path, e)

    return metadata


def get_image_dimensions(image_path: Path) -> Tuple[int, int]:
    """Get image dimensions quickly without full metadata extraction.

    Args:
        image_path: Path to image file

    Returns:
        Tuple of (width, height)
    """
    suffix = image_path.suffix.lower()
    if suffix in (".heic", ".heif"):
        try:
            from pillow_heif import register_heif_opener
            register_heif_opener()
        except ImportError:
            logger.debug("pillow_heif not installed, HEIC/HEIF support unavailable")

    with Image.open(image_path) as img:
        return img.width, img.height
