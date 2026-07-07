"""Vision: image analysis and OCR.

The default engine is ``devthrottle`` - every AI image call goes through the DevThrottle API
vision model (``google/gemma-3-27b-it``) using a DevThrottle API key (``dt_...``), no OpenAI
key required. The ``openai`` and ``claude_code`` engines remain available via the shared
provider abstraction for callers who explicitly ask for them.
"""

from pathlib import Path
from typing import Optional
import sys

try:
    from .devthrottle import DevThrottleVisionClient
except ImportError:
    from src.devthrottle import DevThrottleVisionClient


DEFAULT_ENGINE = "devthrottle"


def _get_shared_provider(engine: str):
    """Load an openai/claude_code provider from cc_shared (path fallback for dev + installed)."""
    try:
        from cc_shared.llm import get_llm_provider
    except ImportError:
        # cc-image/src/vision.py -> cc-image/src -> cc-image -> tools (contains cc_shared)
        src_path = Path(__file__).parent.parent.parent
        if str(src_path) not in sys.path:
            sys.path.insert(0, str(src_path))
        from cc_shared.llm import get_llm_provider
    return get_llm_provider(engine)


def _get_provider(engine: str):
    """Return a provider exposing describe_image/extract_text for the given engine."""
    if engine == "devthrottle":
        return DevThrottleVisionClient()
    return _get_shared_provider(engine)


def describe(image_path: Path, engine: Optional[str] = None) -> str:
    """Get a detailed description of an image.

    Args:
        image_path: Path to the image file.
        engine: 'devthrottle' (default), 'openai', or 'claude_code'.

    Returns:
        Description of the image.
    """
    image_path = Path(image_path)
    if not image_path.exists():
        raise FileNotFoundError(f"Image not found: {image_path}")

    provider = _get_provider(engine or DEFAULT_ENGINE)
    return provider.describe_image(image_path)


def extract_text(image_path: Path, engine: Optional[str] = None) -> str:
    """Extract text from an image (OCR).

    Args:
        image_path: Path to the image file.
        engine: 'devthrottle' (default), 'openai', or 'claude_code'.

    Returns:
        Extracted text from the image.
    """
    image_path = Path(image_path)
    if not image_path.exists():
        raise FileNotFoundError(f"Image not found: {image_path}")

    provider = _get_provider(engine or DEFAULT_ENGINE)
    return provider.extract_text(image_path)
