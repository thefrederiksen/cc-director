"""Vision: image analysis and OCR using LLM providers."""

from pathlib import Path
from typing import Optional
import sys

# Import from cc_shared with path fallback for both development and installed modes
try:
    from cc_shared.llm import get_llm_provider
except ImportError:
    # Add parent paths for development mode
    # cc-image/src/vision.py -> cc-image/src -> cc-image -> src (contains cc_shared)
    src_path = Path(__file__).parent.parent.parent
    if str(src_path) not in sys.path:
        sys.path.insert(0, str(src_path))
    from cc_shared.llm import get_llm_provider


def describe(image_path: Path, engine: Optional[str] = None) -> str:
    """Get detailed description of an image.

    Args:
        image_path: Path to the image file
        engine: LLM provider to use ('claude_code' or 'openai').
                Defaults to 'claude_code'.

    Returns:
        Description of the image
    """
    image_path = Path(image_path)
    if not image_path.exists():
        raise FileNotFoundError(f"Image not found: {image_path}")

    provider = get_llm_provider(engine or "claude_code")
    return provider.describe_image(image_path)


def extract_text(image_path: Path, engine: Optional[str] = None) -> str:
    """Extract text from image (OCR).

    Args:
        image_path: Path to the image file
        engine: LLM provider to use ('claude_code' or 'openai').
                Defaults to 'claude_code'.

    Returns:
        Extracted text from the image
    """
    image_path = Path(image_path)
    if not image_path.exists():
        raise FileNotFoundError(f"Image not found: {image_path}")

    provider = get_llm_provider(engine or "claude_code")
    return provider.extract_text(image_path)
