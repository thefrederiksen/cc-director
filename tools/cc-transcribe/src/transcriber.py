"""Video transcription with timestamps."""

from __future__ import annotations

import json
import logging
import os
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

from openai import OpenAI

from .ffmpeg import extract_audio, get_video_duration
from .screenshots import extract_screenshots, ScreenshotInfo

logger = logging.getLogger(__name__)


@dataclass
class TranscribeResult:
    """Result from transcription."""
    transcript: str
    transcript_path: Path
    segments: list[dict]
    duration: float
    word_count: int
    screenshots: list[ScreenshotInfo] = field(default_factory=list)
    screenshots_dir: Optional[Path] = None


def get_api_key() -> str:
    """Get OpenAI API key from environment."""
    key = os.environ.get("OPENAI_API_KEY")
    if not key:
        raise RuntimeError(
            "OPENAI_API_KEY environment variable not set.\n"
            "Set it with: export OPENAI_API_KEY=your-key-here"
        )
    return key


def whisper_transcribe(audio_path: Path, language: Optional[str] = None) -> dict:
    """Transcribe audio with timestamps using OpenAI Whisper."""
    client = OpenAI(api_key=get_api_key())

    with open(audio_path, "rb") as f:
        kwargs = {
            "model": "whisper-1",
            "file": f,
            "response_format": "verbose_json",
            "timestamp_granularities": ["word", "segment"],
        }
        if language:
            kwargs["language"] = language

        response = client.audio.transcriptions.create(**kwargs)

    # Extract segments
    segments = []
    if hasattr(response, "segments") and response.segments:
        for seg in response.segments:
            segments.append({
                "start": seg.start,
                "end": seg.end,
                "text": seg.text.strip(),
            })

    return {
        "text": response.text,
        "segments": segments,
        "duration": getattr(response, "duration", 0.0),
    }


def segments_to_transcript(segments: list[dict]) -> str:
    """Convert segments to formatted transcript with timestamps."""
    lines = []
    for seg in segments:
        start = seg.get("start", 0)
        minutes = int(start // 60)
        seconds = int(start % 60)
        text = seg.get("text", "").strip()
        if text:
            lines.append(f"[{minutes:02d}:{seconds:02d}] {text}")
    return "\n".join(lines)


def transcribe_video(
    video_path: Path,
    output_dir: Path,
    extract_screenshots_flag: bool = True,
    screenshot_threshold: float = 0.92,
    screenshot_interval: float = 1.0,
    language: Optional[str] = None,
) -> TranscribeResult:
    """
    Transcribe a video file with timestamps and optional screenshots.

    Args:
        video_path: Path to video file
        output_dir: Directory for output files
        extract_screenshots_flag: Extract screenshots at content changes
        screenshot_threshold: SSIM threshold (0-1)
        screenshot_interval: Min seconds between screenshots
        language: Force language (None = auto-detect)

    Returns:
        TranscribeResult with transcript and metadata
    """
    video_path = Path(video_path)
    output_dir = Path(output_dir)

    if not video_path.exists():
        raise FileNotFoundError(f"Video not found: {video_path}")

    output_dir.mkdir(parents=True, exist_ok=True)

    duration = get_video_duration(video_path)
    logger.info(f"Processing: {video_path.name}")
    logger.info(f"Duration: {int(duration//60)}m {int(duration%60)}s")

    # Extract audio
    logger.info("Extracting audio...")
    audio_path = output_dir / f"{video_path.stem}.mp3"
    extract_audio(video_path, audio_path)
    logger.info(f"Audio extracted: {audio_path.stat().st_size / 1024 / 1024:.1f} MB")

    # Transcribe
    logger.info("Transcribing (this may take a while)...")
    result = whisper_transcribe(audio_path, language)
    segments = result.get("segments", [])
    transcript = segments_to_transcript(segments)

    # Save transcript
    transcript_path = output_dir / "transcript.txt"
    transcript_path.write_text(transcript, encoding="utf-8")

    # Save JSON
    json_path = output_dir / "transcript.json"
    json_path.write_text(json.dumps({
        "video": str(video_path),
        "duration": duration,
        "segments": segments,
    }, indent=2, ensure_ascii=False), encoding="utf-8")

    word_count = len(transcript.split())
    logger.info(f"Transcript complete: {word_count} words")

    # Screenshots
    screenshots = []
    screenshots_dir = None

    if extract_screenshots_flag:
        logger.info("Extracting screenshots...")
        screenshots_dir = output_dir / "screenshots"
        _, screenshots = extract_screenshots(
            video_path,
            screenshots_dir,
            threshold=screenshot_threshold,
            min_interval=screenshot_interval
        )

    # Cleanup audio
    audio_path.unlink(missing_ok=True)

    logger.info(f"Complete! Output: {output_dir}")

    return TranscribeResult(
        transcript=transcript,
        transcript_path=transcript_path,
        segments=segments,
        duration=duration,
        word_count=word_count,
        screenshots=screenshots,
        screenshots_dir=screenshots_dir,
    )
