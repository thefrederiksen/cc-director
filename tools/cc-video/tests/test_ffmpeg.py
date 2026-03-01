"""Tests for FFmpeg utilities."""

import pytest
from pathlib import Path
from unittest.mock import patch, MagicMock

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from src.ffmpeg import (
    find_ffmpeg,
    find_ffprobe,
    get_video_info,
    get_duration,
    get_resolution,
    format_duration,
    extract_audio,
)


class TestFormatDuration:
    """Tests for format_duration function."""

    def test_seconds_only(self):
        """Test formatting seconds."""
        assert format_duration(45) == "0:45"

    def test_minutes_and_seconds(self):
        """Test formatting minutes and seconds."""
        assert format_duration(125) == "2:05"

    def test_hours(self):
        """Test formatting hours."""
        assert format_duration(3665) == "1:01:05"

    def test_zero(self):
        """Test zero duration."""
        assert format_duration(0) == "0:00"

    def test_exact_hour(self):
        """Test exact hour."""
        assert format_duration(3600) == "1:00:00"


class TestFindFFmpeg:
    """Tests for find_ffmpeg function."""

    @patch("shutil.which")
    def test_finds_in_path(self, mock_which):
        """Test finding ffmpeg in PATH."""
        mock_which.return_value = "/usr/bin/ffmpeg"
        result = find_ffmpeg()
        assert result == "/usr/bin/ffmpeg"

    @patch("shutil.which")
    @patch("pathlib.Path.exists")
    def test_finds_common_location(self, mock_exists, mock_which):
        """Test finding ffmpeg in common locations."""
        mock_which.return_value = None
        mock_exists.return_value = True
        result = find_ffmpeg()
        assert "ffmpeg" in result.lower()

    @patch("shutil.which")
    @patch("pathlib.Path.exists")
    def test_not_found_raises(self, mock_exists, mock_which):
        """Test error when ffmpeg not found."""
        mock_which.return_value = None
        mock_exists.return_value = False
        with pytest.raises(FileNotFoundError) as exc:
            find_ffmpeg()
        assert "ffmpeg not found" in str(exc.value)


class TestGetVideoInfo:
    """Tests for get_video_info function."""

    def test_file_not_found(self):
        """Test error for missing file."""
        with pytest.raises(FileNotFoundError):
            get_video_info(Path("/nonexistent/video.mp4"))

    @patch("src.ffmpeg.get_duration")
    @patch("src.ffmpeg.get_resolution")
    def test_returns_info_dict(self, mock_res, mock_dur, tmp_path):
        """Test that info dict is returned."""
        video = tmp_path / "test.mp4"
        video.write_bytes(b"fake video data")
        mock_dur.return_value = 120.5
        mock_res.return_value = {"width": 1920, "height": 1080}

        result = get_video_info(video)

        assert result["name"] == "test.mp4"
        assert result["format"] == "mp4"
        assert result["duration"] == 120.5
        assert result["duration_formatted"] == "2:00"
        assert result["width"] == 1920
        assert result["height"] == 1080


class TestExtractAudio:
    """Tests for extract_audio function."""

    def test_file_not_found(self):
        """Test error for missing file."""
        with pytest.raises(FileNotFoundError):
            extract_audio(Path("/nonexistent/video.mp4"))

    @patch("src.ffmpeg.find_ffmpeg")
    @patch("subprocess.run")
    def test_creates_output_dir(self, mock_run, mock_ffmpeg, tmp_path):
        """Test that output directory is created."""
        video = tmp_path / "test.mp4"
        video.write_bytes(b"fake")
        output = tmp_path / "subdir" / "audio.mp3"

        mock_ffmpeg.return_value = "ffmpeg"
        mock_run.return_value = MagicMock(returncode=0)

        extract_audio(video, output)
        assert output.parent.exists()

    @patch("src.ffmpeg.find_ffmpeg")
    @patch("subprocess.run")
    def test_default_output_name(self, mock_run, mock_ffmpeg, tmp_path):
        """Test default output filename."""
        video = tmp_path / "myvideo.mp4"
        video.write_bytes(b"fake")

        mock_ffmpeg.return_value = "ffmpeg"
        mock_run.return_value = MagicMock(returncode=0)

        result = extract_audio(video)
        assert result.stem == "myvideo"
        assert result.suffix == ".mp3"
