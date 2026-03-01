"""Screenshot detection for images.

Uses multiple heuristics to determine if an image is likely a screenshot:
1. Filename patterns (e.g., "Screenshot", "Snip", "Capture")
2. Image dimensions matching common screen resolutions
3. Metadata indicators (no camera info, software indicators)
4. File path patterns
"""

import re
from dataclasses import dataclass
from pathlib import Path
from typing import Optional, Set

from .metadata import ImageMetadata


# Common screen resolutions (width, height)
COMMON_RESOLUTIONS: Set[tuple] = {
    # Standard monitors
    (1920, 1080),  # Full HD
    (2560, 1440),  # QHD
    (3840, 2160),  # 4K
    (1366, 768),   # HD
    (1280, 720),   # HD
    (1440, 900),   # WXGA+
    (1680, 1050),  # WSXGA+
    (1600, 900),   # HD+
    (2560, 1080),  # Ultrawide FHD
    (3440, 1440),  # Ultrawide QHD

    # Retina displays
    (2880, 1800),  # MacBook Pro 15"
    (2560, 1600),  # MacBook Pro 13"
    (3024, 1964),  # MacBook Pro 14"
    (3456, 2234),  # MacBook Pro 16"

    # Mobile
    (1080, 1920),  # Phone portrait FHD
    (1440, 2560),  # Phone portrait QHD
    (1080, 2340),  # iPhone
    (1170, 2532),  # iPhone 12/13
    (1284, 2778),  # iPhone 12/13 Pro Max

    # Tablets
    (2048, 2732),  # iPad Pro 12.9"
    (2388, 1668),  # iPad Pro 11"
    (2360, 1640),  # iPad Air
}

# Screenshot filename patterns
SCREENSHOT_PATTERNS = [
    r"screenshot",
    r"screen[\s_-]?shot",
    r"screen[\s_-]?capture",
    r"screen[\s_-]?grab",
    r"snip",
    r"snipping",
    r"capture[d]?",
    r"clip",
    r"print[\s_-]?screen",
    r"prtsc",
    r"screen[\s_-]?\d{4}",  # Screen 2024...
    r"img[\s_-]?\d{4,}",    # IMG_20240101...
    r"photo[\s_-]?\d{4,}",
]

# Compile patterns for efficiency
SCREENSHOT_REGEX = re.compile(
    "|".join(SCREENSHOT_PATTERNS),
    re.IGNORECASE,
)

# Path patterns that indicate screenshots
SCREENSHOT_PATH_PATTERNS = [
    r"screenshot[s]?",
    r"screen[\s_-]?capture[s]?",
    r"snip[s]?",
    r"clip[s]?",
]

SCREENSHOT_PATH_REGEX = re.compile(
    "|".join(SCREENSHOT_PATH_PATTERNS),
    re.IGNORECASE,
)

# Software that indicates screenshots
SCREENSHOT_SOFTWARE = {
    "snipping tool",
    "greenshot",
    "snagit",
    "lightshot",
    "sharex",
    "gyazo",
    "nimbus",
    "screenclip",
    "monosnap",
    "skitch",
    "cleanshot",
    "dropbox screenshots",
    "windows screen sketch",
}


@dataclass
class ScreenshotResult:
    """Result of screenshot detection."""
    is_screenshot: bool
    confidence: float  # 0.0 to 1.0
    reasons: list


def detect_screenshot(
    file_path: Path,
    metadata: Optional[ImageMetadata] = None,
) -> ScreenshotResult:
    """Detect if an image is likely a screenshot.

    Uses multiple heuristics to build a confidence score:
    - Filename matches screenshot patterns: +0.5
    - Path contains screenshot folder: +0.3
    - Dimensions match screen resolution: +0.2
    - No camera metadata: +0.1
    - Screenshot software in metadata: +0.3

    Args:
        file_path: Path to the image file
        metadata: Optional pre-extracted metadata

    Returns:
        ScreenshotResult with is_screenshot, confidence, and reasons
    """
    confidence = 0.0
    reasons = []

    filename = file_path.name.lower()
    path_str = str(file_path).lower()

    # Check filename patterns
    if SCREENSHOT_REGEX.search(filename):
        confidence += 0.5
        reasons.append("Filename matches screenshot pattern")

    # Check path patterns
    if SCREENSHOT_PATH_REGEX.search(path_str):
        confidence += 0.3
        reasons.append("Path contains screenshot folder")

    # Check dimensions if we have metadata
    if metadata:
        dims = (metadata.width, metadata.height)
        dims_reversed = (metadata.height, metadata.width)

        if dims in COMMON_RESOLUTIONS or dims_reversed in COMMON_RESOLUTIONS:
            confidence += 0.2
            reasons.append(f"Dimensions match screen resolution: {dims[0]}x{dims[1]}")

        # No camera info often indicates screenshot
        if not metadata.camera_make and not metadata.camera_model:
            # Only add this as a weak signal
            confidence += 0.1
            reasons.append("No camera metadata")

        # Check for screenshot software in EXIF
        if metadata.raw_exif:
            software = str(metadata.raw_exif.get("Software", "")).lower()
            for ss_software in SCREENSHOT_SOFTWARE:
                if ss_software in software:
                    confidence += 0.3
                    reasons.append(f"Screenshot software detected: {ss_software}")
                    break

    # Cap confidence at 1.0
    confidence = min(confidence, 1.0)

    # Threshold for screenshot detection
    is_screenshot = confidence >= 0.4

    return ScreenshotResult(
        is_screenshot=is_screenshot,
        confidence=confidence,
        reasons=reasons,
    )


def is_screenshot(file_path: Path, metadata: Optional[ImageMetadata] = None) -> bool:
    """Quick check if an image is a screenshot.

    Args:
        file_path: Path to the image file
        metadata: Optional pre-extracted metadata

    Returns:
        True if the image is likely a screenshot
    """
    result = detect_screenshot(file_path, metadata)
    return result.is_screenshot
