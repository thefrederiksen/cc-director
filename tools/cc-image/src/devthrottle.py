"""DevThrottle vision client - routes cc-image AI calls through the DevThrottle API.

Every AI image call in cc-image goes through the DevThrottle inference gateway using a
DevThrottle API key (``dt_...``), NOT OpenAI. This module talks to the OpenAI-compatible
chat completions endpoint (``POST {base}/chat/completions``) with the live vision model
``google/gemma-3-27b-it``, sending the image as an OpenAI ``image_url`` content block
(a base64 data URI). Images are downscaled before sending to cut cost and latency.

Configuration (all overridable so the client can point at local/staging):
- ``DEVTHROTTLE_API_KEY``   - the ``dt_`` key, presented as the Bearer token. Required.
- ``DEVTHROTTLE_API_BASE``  - the OpenAI-compatible ``/v1`` base URL. Default production.
- ``DEVTHROTTLE_VISION_MODEL`` - the vision model id. Default ``google/gemma-3-27b-it``.

The credential is Bearer-presented and NEVER logged.
"""

import base64
import io
import os
from pathlib import Path
from typing import Optional

import requests
from PIL import Image


# Production DevThrottle API base (OpenAI-compatible /v1). Matches the .NET Gateway constant
# TranscriptionEndpointResolver.DevThrottleBaseUrl.
DEFAULT_API_BASE = "https://devthrottle.com/api/v1"

# The live DevThrottle vision model, verified to accept image input via the chat endpoint.
DEFAULT_VISION_MODEL = "google/gemma-3-27b-it"

# Long-edge pixel cap for the image we send. ~896px is plenty for "what's in it" and keeps
# the base64 payload small (cost + speed).
DEFAULT_MAX_EDGE = 896

DESCRIBE_PROMPT = (
    "Describe this image in detail. Include: main subjects, colors, "
    "setting/location, mood, any text visible, and notable details. "
    "Be concise but thorough. Output ONLY the description, no preamble."
)

OCR_PROMPT = (
    "Extract all text in this image verbatim, preserving line breaks. "
    "Return only the text, nothing else. No preamble, no explanation."
)


class DevThrottleConfigError(ValueError):
    """Raised when the DevThrottle API key or base URL is missing or malformed."""


class DevThrottleTransientError(RuntimeError):
    """Raised for a retriable API response (rate limit or upstream 5xx).

    Batch mode backs off and retries these; a single-image call surfaces them.
    """


# HTTP statuses worth retrying: rate limit plus transient upstream failures.
RETRIABLE_STATUS = frozenset({429, 500, 502, 503, 504})


def get_api_key() -> str:
    """Return the DevThrottle API key from the environment.

    Raises:
        DevThrottleConfigError: if ``DEVTHROTTLE_API_KEY`` is not set.
    """
    key = os.environ.get("DEVTHROTTLE_API_KEY", "").strip()
    if not key:
        raise DevThrottleConfigError(
            "DEVTHROTTLE_API_KEY environment variable not set. "
            "Set it to your DevThrottle API key (dt_...) from https://devthrottle.com."
        )
    return key


def get_api_base() -> str:
    """Return the configured DevThrottle API base URL (no trailing slash)."""
    base = os.environ.get("DEVTHROTTLE_API_BASE", "").strip() or DEFAULT_API_BASE
    return base.rstrip("/")


def get_vision_model() -> str:
    """Return the configured DevThrottle vision model id."""
    return os.environ.get("DEVTHROTTLE_VISION_MODEL", "").strip() or DEFAULT_VISION_MODEL


def encode_image_data_uri(image_path: Path, max_edge: int = DEFAULT_MAX_EDGE) -> str:
    """Downscale an image and return it as a base64 JPEG data URI.

    The image is opened, flattened onto white if it has transparency, downscaled so its
    long edge is at most ``max_edge`` pixels, and re-encoded as JPEG. This keeps the payload
    small without losing what a vision model needs to understand the picture.

    Args:
        image_path: Path to the source image.
        max_edge: Maximum length (pixels) of the longer edge after downscaling.

    Returns:
        A ``data:image/jpeg;base64,...`` string.
    """
    with Image.open(image_path) as img:
        img = _flatten_to_rgb(img)

        long_edge = max(img.size)
        if long_edge > max_edge:
            ratio = max_edge / long_edge
            target = (max(1, int(img.size[0] * ratio)), max(1, int(img.size[1] * ratio)))
            img = img.resize(target, Image.Resampling.LANCZOS)

        buffer = io.BytesIO()
        img.save(buffer, format="JPEG", quality=85, optimize=True)
        encoded = base64.b64encode(buffer.getvalue()).decode("ascii")

    return f"data:image/jpeg;base64,{encoded}"


def _flatten_to_rgb(img: Image.Image) -> Image.Image:
    """Return an RGB copy, compositing transparency onto a white background."""
    if img.mode in ("RGBA", "LA") or (img.mode == "P" and "transparency" in img.info):
        rgba = img.convert("RGBA")
        background = Image.new("RGB", rgba.size, (255, 255, 255))
        background.paste(rgba, mask=rgba.split()[3])
        return background
    if img.mode != "RGB":
        return img.convert("RGB")
    return img.copy()


class DevThrottleVisionClient:
    """Client for the DevThrottle OpenAI-compatible chat completions vision endpoint."""

    def __init__(
        self,
        api_key: Optional[str] = None,
        api_base: Optional[str] = None,
        model: Optional[str] = None,
        max_edge: int = DEFAULT_MAX_EDGE,
        timeout: int = 120,
    ):
        self.api_key = api_key or get_api_key()
        self.api_base = (api_base or get_api_base()).rstrip("/")
        self.model = model or get_vision_model()
        self.max_edge = max_edge
        self.timeout = timeout

    @property
    def chat_url(self) -> str:
        return f"{self.api_base}/chat/completions"

    def describe_image(self, image_path: Path, prompt: Optional[str] = None) -> str:
        """Return a detailed description of an image."""
        return self._vision_call(image_path, prompt or DESCRIBE_PROMPT, max_tokens=500)

    def extract_text(self, image_path: Path, prompt: Optional[str] = None) -> str:
        """Return all text extracted from an image (OCR)."""
        return self._vision_call(image_path, prompt or OCR_PROMPT, max_tokens=1500)

    def _vision_call(self, image_path: Path, prompt: str, max_tokens: int) -> str:
        image_path = Path(image_path)
        if not image_path.exists():
            raise FileNotFoundError(f"Image not found: {image_path}")

        data_uri = encode_image_data_uri(image_path, max_edge=self.max_edge)

        payload = {
            "model": self.model,
            "messages": [
                {
                    "role": "user",
                    "content": [
                        {"type": "text", "text": prompt},
                        {"type": "image_url", "image_url": {"url": data_uri}},
                    ],
                }
            ],
            "max_tokens": max_tokens,
            "stream": False,
        }

        response = requests.post(
            self.chat_url,
            headers={
                "Content-Type": "application/json",
                "Authorization": f"Bearer {self.api_key}",
            },
            json=payload,
            timeout=self.timeout,
        )

        if response.status_code != 200:
            message = (
                f"DevThrottle API error: {response.status_code} {response.reason}. "
                f"{_short_body(response.text)}"
            )
            if response.status_code in RETRIABLE_STATUS:
                raise DevThrottleTransientError(message)
            raise RuntimeError(message)

        return _extract_content(response.json())


def _extract_content(body: dict) -> str:
    """Pull ``choices[0].message.content`` out of an OpenAI-compatible chat response."""
    choices = body.get("choices")
    if not choices:
        raise RuntimeError("DevThrottle API returned no choices in the response.")

    message = choices[0].get("message") or {}
    content = message.get("content")
    if not isinstance(content, str) or not content.strip():
        raise RuntimeError("DevThrottle API returned an empty message for the image.")

    return content.strip()


def _short_body(text: str, limit: int = 300) -> str:
    """Trim a response body for error messages (never leaks the key - it is not in the body)."""
    text = (text or "").strip()
    return text if len(text) <= limit else text[:limit] + "..."
