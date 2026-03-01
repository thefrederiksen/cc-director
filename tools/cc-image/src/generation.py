"""DALL-E image generation."""

import os
from pathlib import Path
from typing import Literal

import requests


DalleSize = Literal["1024x1024", "1024x1792", "1792x1024"]
DalleQuality = Literal["standard", "hd"]


def get_api_key() -> str:
    """Get OpenAI API key from environment."""
    key = os.environ.get("OPENAI_API_KEY")
    if not key:
        raise RuntimeError("OPENAI_API_KEY environment variable not set")
    return key


def generate(
    prompt: str,
    size: DalleSize = "1024x1024",
    quality: DalleQuality = "standard",
    model: str = "dall-e-3",
) -> bytes:
    """Generate image with DALL-E."""
    response = requests.post(
        "https://api.openai.com/v1/images/generations",
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
        raise RuntimeError(f"DALL-E error: {response.text}")

    result = response.json()
    if "data" not in result or not result["data"]:
        raise RuntimeError("No image generated")

    image_url = result["data"][0]["url"]
    image_response = requests.get(image_url, timeout=60)

    if image_response.status_code != 200:
        raise RuntimeError("Failed to download generated image")

    return image_response.content


def generate_to_file(
    prompt: str,
    output_path: Path,
    size: DalleSize = "1024x1024",
    quality: DalleQuality = "standard",
) -> Path:
    """Generate image and save to file."""
    output_path = Path(output_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    image_bytes = generate(prompt, size, quality)
    output_path.write_bytes(image_bytes)

    return output_path
