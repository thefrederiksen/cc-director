"""Screenshot extraction from video."""

from dataclasses import dataclass
from pathlib import Path
from typing import Optional

import cv2
import numpy as np
from PIL import Image
from skimage.metrics import structural_similarity as ssim


@dataclass
class Screenshot:
    """Screenshot metadata."""
    path: Path
    timestamp: float
    timestamp_formatted: str


def format_timestamp(seconds: float) -> str:
    """Format seconds as HH-MM-SS."""
    h = int(seconds // 3600)
    m = int((seconds % 3600) // 60)
    s = int(seconds % 60)
    return f"{h:02d}-{m:02d}-{s:02d}"


def calculate_similarity(frame1: np.ndarray, frame2: np.ndarray) -> float:
    """Calculate SSIM between frames."""
    gray1 = cv2.cvtColor(frame1, cv2.COLOR_BGR2GRAY)
    gray2 = cv2.cvtColor(frame2, cv2.COLOR_BGR2GRAY)

    # Resize for speed
    h, w = gray1.shape
    if w > 1280:
        scale = 1280 / w
        size = (1280, int(h * scale))
        gray1 = cv2.resize(gray1, size)
        gray2 = cv2.resize(gray2, size)

    return ssim(gray1, gray2)


def extract_screenshots(
    video_path: Path,
    output_dir: Path,
    threshold: float = 0.92,
    interval: float = 1.0,
    max_screenshots: Optional[int] = None,
) -> list[Screenshot]:
    """
    Extract screenshots when content changes.

    Args:
        video_path: Path to video
        output_dir: Directory for screenshots
        threshold: SSIM threshold (lower = more sensitive)
        interval: Minimum seconds between screenshots
        max_screenshots: Maximum number to extract

    Returns:
        List of Screenshot objects
    """
    video_path = Path(video_path)
    if not video_path.exists():
        raise FileNotFoundError(f"Video not found: {video_path}")

    cap = cv2.VideoCapture(str(video_path))
    if not cap.isOpened():
        raise ValueError(f"Cannot open video: {video_path}")

    fps = cap.get(cv2.CAP_PROP_FPS)
    total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))

    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    screenshots: list[Screenshot] = []
    last_frame = None
    last_time = -interval
    frame_num = 0

    try:
        while True:
            ret, frame = cap.read()
            if not ret:
                break

            current_time = frame_num / fps

            if current_time - last_time >= interval:
                save = False

                if last_frame is None:
                    save = True
                else:
                    sim = calculate_similarity(frame, last_frame)
                    if sim < threshold:
                        save = True

                if save:
                    ts_str = format_timestamp(current_time)
                    filepath = output_dir / f"frame_{ts_str}.png"

                    rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
                    Image.fromarray(rgb).save(filepath, "PNG")

                    screenshots.append(Screenshot(
                        path=filepath,
                        timestamp=current_time,
                        timestamp_formatted=ts_str,
                    ))

                    last_frame = frame.copy()
                    last_time = current_time

                    if max_screenshots and len(screenshots) >= max_screenshots:
                        break

            frame_num += 1
    finally:
        cap.release()

    return screenshots


def extract_frame_at(video_path: Path, timestamp: float, output_path: Path) -> Path:
    """Extract single frame at specific timestamp."""
    video_path = Path(video_path)
    if not video_path.exists():
        raise FileNotFoundError(f"Video not found: {video_path}")

    cap = cv2.VideoCapture(str(video_path))
    if not cap.isOpened():
        raise ValueError(f"Cannot open video: {video_path}")

    fps = cap.get(cv2.CAP_PROP_FPS)
    target_frame = int(timestamp * fps)

    cap.set(cv2.CAP_PROP_POS_FRAMES, target_frame)
    ret, frame = cap.read()
    cap.release()

    if not ret:
        raise ValueError(f"Cannot read frame at {timestamp}s")

    output_path = Path(output_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
    Image.fromarray(rgb).save(output_path, "PNG")

    return output_path
