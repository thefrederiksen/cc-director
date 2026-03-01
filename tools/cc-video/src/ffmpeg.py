"""FFmpeg utilities for video operations."""

import os
import re
import shutil
import subprocess
from pathlib import Path
from typing import Literal, Optional


AudioFormat = Literal["mp3", "wav", "aac", "flac", "ogg"]


def find_ffmpeg() -> str:
    """Find ffmpeg executable."""
    ffmpeg = shutil.which("ffmpeg")
    if ffmpeg:
        return ffmpeg

    paths = [
        "C:/ffmpeg/bin/ffmpeg.exe",
        "C:/Program Files/ffmpeg/bin/ffmpeg.exe",
        "/usr/bin/ffmpeg",
        "/usr/local/bin/ffmpeg",
    ]

    for path in paths:
        if Path(path).exists():
            return path

    raise FileNotFoundError(
        "ffmpeg not found. Install from: https://ffmpeg.org/download.html"
    )


def find_ffprobe() -> Optional[str]:
    """Find ffprobe executable."""
    ffprobe = shutil.which("ffprobe")
    if ffprobe:
        return ffprobe

    # Try next to ffmpeg
    ffmpeg = find_ffmpeg()
    ffprobe_path = Path(ffmpeg).parent / Path(ffmpeg).name.replace("ffmpeg", "ffprobe")
    if ffprobe_path.exists():
        return str(ffprobe_path)

    return None


def get_video_info(video_path: Path) -> dict:
    """Get video file information."""
    video_path = Path(video_path)
    if not video_path.exists():
        raise FileNotFoundError(f"Video not found: {video_path}")

    duration = get_duration(video_path)
    size = video_path.stat().st_size

    # Try to get resolution with ffprobe
    resolution = get_resolution(video_path)

    return {
        "path": str(video_path),
        "name": video_path.name,
        "format": video_path.suffix.lstrip("."),
        "duration": duration,
        "duration_formatted": format_duration(duration),
        "size_bytes": size,
        "size_mb": round(size / 1024 / 1024, 2),
        "width": resolution.get("width"),
        "height": resolution.get("height"),
    }


def get_duration(video_path: Path) -> float:
    """Get video duration in seconds."""
    ffprobe = find_ffprobe()

    if ffprobe:
        cmd = [
            ffprobe, "-v", "error",
            "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1",
            str(video_path)
        ]
        result = subprocess.run(cmd, capture_output=True, text=True)
        if result.returncode == 0 and result.stdout.strip():
            return float(result.stdout.strip())

    # Fallback: parse ffmpeg output
    ffmpeg = find_ffmpeg()
    cmd = [ffmpeg, "-i", str(video_path), "-f", "null", "-"]
    result = subprocess.run(cmd, capture_output=True, text=True)

    match = re.search(r"Duration: (\d+):(\d+):(\d+)\.(\d+)", result.stderr)
    if match:
        h, m, s, cs = map(int, match.groups())
        return h * 3600 + m * 60 + s + cs / 100

    return 0.0


def get_resolution(video_path: Path) -> dict:
    """Get video resolution."""
    ffprobe = find_ffprobe()
    if not ffprobe:
        return {}

    cmd = [
        ffprobe, "-v", "error",
        "-select_streams", "v:0",
        "-show_entries", "stream=width,height",
        "-of", "csv=p=0:s=x",
        str(video_path)
    ]
    result = subprocess.run(cmd, capture_output=True, text=True)

    if result.returncode == 0 and "x" in result.stdout:
        parts = result.stdout.strip().split("x")
        if len(parts) == 2:
            return {"width": int(parts[0]), "height": int(parts[1])}

    return {}


def format_duration(seconds: float) -> str:
    """Format seconds as HH:MM:SS."""
    h = int(seconds // 3600)
    m = int((seconds % 3600) // 60)
    s = int(seconds % 60)
    if h > 0:
        return f"{h}:{m:02d}:{s:02d}"
    return f"{m}:{s:02d}"


def extract_audio(
    video_path: Path,
    output_path: Optional[Path] = None,
    format: AudioFormat = "mp3",
    bitrate: str = "192k",
) -> Path:
    """Extract audio from video."""
    video_path = Path(video_path)
    if not video_path.exists():
        raise FileNotFoundError(f"Video not found: {video_path}")

    if output_path is None:
        output_path = video_path.with_suffix(f".{format}")
    else:
        output_path = Path(output_path)

    output_path.parent.mkdir(parents=True, exist_ok=True)

    codecs = {
        "mp3": "libmp3lame",
        "wav": "pcm_s16le",
        "aac": "aac",
        "flac": "flac",
        "ogg": "libvorbis",
    }

    ffmpeg = find_ffmpeg()
    cmd = [
        ffmpeg, "-i", str(video_path),
        "-vn", "-acodec", codecs.get(format, "libmp3lame"),
        "-ab", bitrate, "-y",
        str(output_path)
    ]

    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(f"ffmpeg failed: {result.stderr}")

    return output_path
