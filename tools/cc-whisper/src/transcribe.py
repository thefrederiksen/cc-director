"""Audio transcription using OpenAI Whisper API."""

import json
import os
from pathlib import Path
from typing import Any, Dict, Optional

from openai import OpenAI


def get_api_key() -> str:
    """Get OpenAI API key from environment."""
    key = os.environ.get("OPENAI_API_KEY")
    if not key:
        raise RuntimeError("OPENAI_API_KEY environment variable not set")
    return key


def transcribe(
    audio_path: Path,
    language: Optional[str] = None,
    timestamps: bool = False,
    prompt: Optional[str] = None,
    temperature: Optional[float] = None,
) -> Dict[str, Any]:
    """
    Transcribe audio file.

    Args:
        audio_path: Path to audio file
        language: Language code (e.g., "en", "es") or None for auto-detect
        timestamps: Include word-level timestamps
        prompt: Context hint to improve accuracy (names, terms, jargon)
        temperature: Sampling temperature 0-1 (0=deterministic, default=0)

    Returns:
        Dict with 'text' and optionally 'words' and 'segments'
    """
    audio_path = Path(audio_path)
    if not audio_path.exists():
        raise FileNotFoundError(f"Audio file not found: {audio_path}")

    client = OpenAI(api_key=get_api_key())

    with open(audio_path, "rb") as f:
        kwargs = {
            "model": "whisper-1",
            "file": f,
        }

        if language:
            kwargs["language"] = language

        if prompt:
            kwargs["prompt"] = prompt

        if temperature is not None:
            kwargs["temperature"] = temperature

        if timestamps:
            kwargs["response_format"] = "verbose_json"
            kwargs["timestamp_granularities"] = ["word", "segment"]

        response = client.audio.transcriptions.create(**kwargs)

    if timestamps:
        words = []
        if hasattr(response, "words") and response.words:
            for w in response.words:
                words.append({
                    "word": w.word,
                    "start": w.start,
                    "end": w.end,
                })

        segments = []
        if hasattr(response, "segments") and response.segments:
            for s in response.segments:
                segments.append({
                    "start": s.start,
                    "end": s.end,
                    "text": s.text.strip(),
                })

        return {
            "text": response.text,
            "words": words,
            "segments": segments,
            "duration": getattr(response, "duration", 0.0),
        }

    return {"text": response.text}


def transcribe_to_file(
    audio_path: Path,
    output_path: Path,
    language: Optional[str] = None,
    timestamps: bool = False,
    prompt: Optional[str] = None,
    temperature: Optional[float] = None,
) -> Path:
    """
    Transcribe audio and save to file.

    Args:
        audio_path: Path to audio file
        output_path: Path to output text file
        language: Language code (e.g., "en", "es") or None for auto-detect
        timestamps: Include timestamps in output
        prompt: Context hint to improve accuracy
        temperature: Sampling temperature 0-1

    Returns:
        Path to the output file
    """
    result = transcribe(audio_path, language, timestamps, prompt, temperature)

    output_path = Path(output_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    if timestamps and result.get("segments"):
        # Format with timestamps
        lines = []
        for seg in result["segments"]:
            start = seg["start"]
            mins = int(start // 60)
            secs = int(start % 60)
            lines.append(f"[{mins:02d}:{secs:02d}] {seg['text']}")
        output_path.write_text("\n".join(lines), encoding="utf-8")
    else:
        output_path.write_text(result["text"], encoding="utf-8")

    return output_path


def transcribe_formatted(
    audio_path: Path,
    format: str = "txt",
    language: Optional[str] = None,
    prompt: Optional[str] = None,
    temperature: Optional[float] = None,
) -> str:
    """
    Transcribe audio with specific output format.

    Args:
        audio_path: Path to audio file
        format: Output format - txt, srt, vtt, or json
        language: Language code (e.g., "en", "es") or None for auto-detect
        prompt: Context hint to improve accuracy
        temperature: Sampling temperature 0-1

    Returns:
        Transcription in the requested format
    """
    audio_path = Path(audio_path)
    if not audio_path.exists():
        raise FileNotFoundError(f"Audio file not found: {audio_path}")

    if format not in ("txt", "srt", "vtt", "json"):
        raise ValueError(f"Unknown format: {format}. Use txt, srt, vtt, or json.")

    client = OpenAI(api_key=get_api_key())

    # Map our format names to API response_format values
    format_map = {
        "txt": "text",
        "srt": "srt",
        "vtt": "vtt",
        "json": "verbose_json",
    }

    with open(audio_path, "rb") as f:
        kwargs = {
            "model": "whisper-1",
            "file": f,
            "response_format": format_map[format],
        }

        if language:
            kwargs["language"] = language
        if prompt:
            kwargs["prompt"] = prompt
        if temperature is not None:
            kwargs["temperature"] = temperature

        response = client.audio.transcriptions.create(**kwargs)

    # srt/vtt/text return string directly, verbose_json returns object
    if format == "json":
        return json.dumps({
            "text": response.text,
            "duration": getattr(response, "duration", 0.0),
            "language": getattr(response, "language", None),
        }, indent=2, ensure_ascii=False)

    return response


def translate(
    audio_path: Path,
    prompt: Optional[str] = None,
    temperature: Optional[float] = None,
) -> Dict[str, Any]:
    """
    Translate audio from any language to English.

    Args:
        audio_path: Path to audio file (any language)
        prompt: Context hint in English to guide translation style
        temperature: Sampling temperature 0-1

    Returns:
        Dict with 'text' containing English translation
    """
    audio_path = Path(audio_path)
    if not audio_path.exists():
        raise FileNotFoundError(f"Audio file not found: {audio_path}")

    client = OpenAI(api_key=get_api_key())

    with open(audio_path, "rb") as f:
        kwargs = {
            "model": "whisper-1",
            "file": f,
        }

        if prompt:
            kwargs["prompt"] = prompt
        if temperature is not None:
            kwargs["temperature"] = temperature

        response = client.audio.translations.create(**kwargs)

    return {"text": response.text}
