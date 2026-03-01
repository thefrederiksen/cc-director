"""Text-to-speech using OpenAI TTS API."""

import logging
import os
import re
from pathlib import Path
from typing import Literal

from openai import OpenAI

logger = logging.getLogger(__name__)


Voice = Literal["alloy", "echo", "fable", "nova", "onyx", "shimmer"]
Model = Literal["tts-1", "tts-1-hd"]

# OpenAI TTS has a 4096 character limit
MAX_CHARS = 4096


def get_api_key() -> str:
    """Get OpenAI API key from environment."""
    key = os.environ.get("OPENAI_API_KEY")
    if not key:
        raise RuntimeError("OPENAI_API_KEY environment variable not set")
    return key


def clean_text(text: str) -> str:
    """Clean markdown and special characters for speech."""
    # Remove code blocks
    text = re.sub(r'```[\s\S]*?```', '', text)
    text = re.sub(r'`[^`]+`', '', text)

    # Remove markdown formatting
    text = re.sub(r'\*\*([^*]+)\*\*', r'\1', text)  # Bold
    text = re.sub(r'\*([^*]+)\*', r'\1', text)      # Italic
    text = re.sub(r'__([^_]+)__', r'\1', text)      # Bold
    text = re.sub(r'_([^_]+)_', r'\1', text)        # Italic
    text = re.sub(r'~~([^~]+)~~', r'\1', text)      # Strikethrough

    # Remove links but keep text
    text = re.sub(r'\[([^\]]+)\]\([^)]+\)', r'\1', text)

    # Remove headers
    text = re.sub(r'^#+\s*', '', text, flags=re.MULTILINE)

    # Remove horizontal rules
    text = re.sub(r'^[-*_]{3,}\s*$', '', text, flags=re.MULTILINE)

    # Clean up whitespace
    text = re.sub(r'\n{3,}', '\n\n', text)
    text = text.strip()

    return text


def chunk_text(text: str, max_chars: int = MAX_CHARS) -> list[str]:
    """Split text into chunks at sentence boundaries."""
    if len(text) <= max_chars:
        return [text]

    chunks = []
    sentences = re.split(r'(?<=[.!?])\s+', text)
    current = ""

    for sentence in sentences:
        if len(current) + len(sentence) + 1 <= max_chars:
            current = f"{current} {sentence}".strip()
        else:
            if current:
                chunks.append(current)
            current = sentence

    if current:
        chunks.append(current)

    return chunks


def tts(
    text: str,
    voice: Voice = "onyx",
    model: Model = "tts-1",
    speed: float = 1.0,
    clean_markdown: bool = True,
) -> bytes:
    """
    Convert text to speech.

    Args:
        text: Text to convert
        voice: Voice to use (alloy, echo, fable, nova, onyx, shimmer)
        model: TTS model (tts-1, tts-1-hd)
        speed: Speech speed (0.25 to 4.0)
        clean_markdown: Remove markdown formatting

    Returns:
        Audio bytes (MP3 format)
    """
    if clean_markdown:
        text = clean_text(text)

    if not text.strip():
        raise ValueError("No text to convert after cleaning")

    client = OpenAI(api_key=get_api_key())

    # Handle long text
    chunks = chunk_text(text)

    if len(chunks) == 1:
        response = client.audio.speech.create(
            model=model,
            voice=voice,
            input=text,
            speed=speed,
        )
        return response.content

    # Multiple chunks - concatenate
    audio_parts = []
    for i, chunk in enumerate(chunks):
        logger.info(f"Processing chunk {i+1}/{len(chunks)}")
        response = client.audio.speech.create(
            model=model,
            voice=voice,
            input=chunk,
            speed=speed,
        )
        audio_parts.append(response.content)

    return b''.join(audio_parts)


def tts_to_file(
    text: str,
    output_path: Path,
    voice: Voice = "onyx",
    model: Model = "tts-1",
    speed: float = 1.0,
    clean_markdown: bool = True,
) -> Path:
    """Convert text to speech and save to file."""
    output_path = Path(output_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    audio = tts(text, voice, model, speed, clean_markdown)
    output_path.write_bytes(audio)

    return output_path
