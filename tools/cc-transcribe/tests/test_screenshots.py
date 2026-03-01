"""Tests for screenshot extraction in cc-transcribe."""

import pytest
from pathlib import Path
import numpy as np

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from src.screenshots import format_timestamp, calculate_similarity


class TestFormatTimestamp:
    """Tests for format_timestamp function."""

    def test_zero(self):
        """Test zero seconds."""
        result = format_timestamp(0)
        assert result == "00-00-00"

    def test_seconds_only(self):
        """Test just seconds."""
        result = format_timestamp(45)
        assert result == "00-00-45"

    def test_minutes_and_seconds(self):
        """Test minutes and seconds."""
        result = format_timestamp(125)
        assert result == "00-02-05"

    def test_hours(self):
        """Test hours formatting."""
        result = format_timestamp(3665)
        assert result == "01-01-05"


class TestCalculateSimilarity:
    """Tests for calculate_similarity (SSIM) function."""

    def test_identical_frames(self):
        """Test that identical frames have high similarity."""
        frame = np.random.randint(0, 255, (480, 640, 3), dtype=np.uint8)
        similarity = calculate_similarity(frame, frame.copy())
        assert similarity > 0.99

    def test_different_frames(self):
        """Test that different frames have low similarity."""
        frame1 = np.zeros((480, 640, 3), dtype=np.uint8)
        frame2 = np.ones((480, 640, 3), dtype=np.uint8) * 255
        similarity = calculate_similarity(frame1, frame2)
        assert similarity < 0.5

    def test_similar_frames(self):
        """Test that similar frames have moderate similarity."""
        frame1 = np.full((480, 640, 3), 100, dtype=np.uint8)
        frame2 = np.full((480, 640, 3), 110, dtype=np.uint8)
        similarity = calculate_similarity(frame1, frame2)
        assert 0.5 < similarity < 1.0

    def test_large_frame_resized(self):
        """Test that large frames are resized for performance."""
        frame = np.random.randint(0, 255, (1080, 1920, 3), dtype=np.uint8)
        similarity = calculate_similarity(frame, frame.copy())
        assert similarity > 0.99


class TestExtractScreenshots:
    """Tests for extract_screenshots function."""

    def test_video_not_found(self):
        """Test error for missing video file."""
        from src.screenshots import extract_screenshots
        with pytest.raises(FileNotFoundError):
            extract_screenshots(Path("/nonexistent.mp4"), Path("/output"))

    def test_invalid_video_raises_error(self, tmp_path):
        """Test that invalid video raises ValueError."""
        from src.screenshots import extract_screenshots

        video = tmp_path / "test.mp4"
        video.write_bytes(b"not a real video")
        output_dir = tmp_path / "screenshots"

        # Invalid video should raise ValueError
        with pytest.raises(ValueError):
            extract_screenshots(video, output_dir)
