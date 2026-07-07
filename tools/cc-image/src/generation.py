"""Text-to-image generation via the DevThrottle images endpoint.

DEFERRED (tracked dependency): the DevThrottle text-to-image endpoint
``POST {base}/images/generations`` is planned but NOT yet built on the API. This module is
wired to call it - using the same DevThrottle API key and base URL as the vision path - but
until the endpoint ships the call will fail with a clear error. It does NOT fall back to
OpenAI / DALL-E (that dependency was removed as part of issue #1141).

Do not block ``describe``/``ocr`` on this - those are live now.
"""

from pathlib import Path
from typing import Literal

import requests

try:
    from .devthrottle import get_api_key, get_api_base
except ImportError:
    from src.devthrottle import get_api_key, get_api_base


ImageSize = Literal["1024x1024", "1024x1792", "1792x1024"]
ImageQuality = Literal["standard", "hd"]

# Set true once the DevThrottle images endpoint is live. Until then, generate() refuses up
# front with a clear message instead of making a call that is known to fail.
IMAGES_ENDPOINT_AVAILABLE = False


class GenerationNotAvailableError(RuntimeError):
    """Raised because the DevThrottle text-to-image endpoint is not built yet."""


def generate(
    prompt: str,
    size: ImageSize = "1024x1024",
    quality: ImageQuality = "standard",
    model: str = "devthrottle-image",
) -> bytes:
    """Generate an image from a text prompt via the DevThrottle images endpoint.

    Raises:
        GenerationNotAvailableError: while the endpoint is not yet built.
        RuntimeError: if the endpoint returns a non-success response.
    """
    if not IMAGES_ENDPOINT_AVAILABLE:
        raise GenerationNotAvailableError(
            "Image generation is deferred: the DevThrottle text-to-image endpoint "
            "(POST /v1/images/generations) is not built yet. Tracked as a dependency of "
            "issue #1141. The 'describe' and 'ocr' commands are live now."
        )

    response = requests.post(
        f"{get_api_base()}/images/generations",
        headers={
            "Content-Type": "application/json",
            "Authorization": f"Bearer {get_api_key()}",
        },
        json={
            "model": model,
            "prompt": prompt,
            "n": 1,
            "size": size,
            "quality": quality,
        },
        timeout=120,
    )

    if response.status_code != 200:
        raise RuntimeError(f"DevThrottle images error: {response.status_code} {response.text}")

    result = response.json()
    data = result.get("data") or []
    if not data:
        raise RuntimeError("No image generated")

    first = data[0]
    # OpenAI-compatible responses return either a URL or inline base64.
    if first.get("b64_json"):
        import base64
        return base64.b64decode(first["b64_json"])

    image_url = first.get("url")
    if not image_url:
        raise RuntimeError("No image URL or base64 payload in the response")

    image_response = requests.get(image_url, timeout=60)
    if image_response.status_code != 200:
        raise RuntimeError("Failed to download generated image")

    return image_response.content


def generate_to_file(
    prompt: str,
    output_path: Path,
    size: ImageSize = "1024x1024",
    quality: ImageQuality = "standard",
) -> Path:
    """Generate an image and save it to a file."""
    output_path = Path(output_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    image_bytes = generate(prompt, size, quality)
    output_path.write_bytes(image_bytes)

    return output_path
