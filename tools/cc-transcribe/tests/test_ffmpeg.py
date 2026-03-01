"""Tests for FFmpeg utilities in cc-transcribe."""

import pytest
from pathlib import Path
from unittest.mock import patch, MagicMock

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from src.ffmpeg import find_ffmpeg


class TestFindFFmpeg:
    """Tests for find_ffmpeg function."""

    @patch("shutil.which")
    def test_finds_in_path(self, mock_which):
        """Test finding ffmpeg in PATH."""
        mock_which.return_value = "/usr/local/bin/ffmpeg"
        result = find_ffmpeg()
        assert result == "/usr/local/bin/ffmpeg"

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
        assert "ffmpeg not found" in str(exc.value).lower()




class TestExtractAudio:
    """Tests for extract_audio function."""

    def test_video_not_found(self):
        """Test error for missing video file."""
        from src.ffmpeg import extract_audio
        with pytest.raises(FileNotFoundError):
            extract_audio(Path("/nonexistent/video.mp4"), Path("/output.mp3"))

    @patch("src.ffmpeg.find_ffmpeg")
    @patch("subprocess.run")
    def test_creates_output_directory(self, mock_run, mock_ffmpeg, tmp_path):
        """Test that output directory is created."""
        from src.ffmpeg import extract_audio

        video = tmp_path / "test.mp4"
        video.write_bytes(b"fake video")
        output = tmp_path / "subdir" / "audio.mp3"

        mock_ffmpeg.return_value = "ffmpeg"
        mock_run.return_value = MagicMock(returncode=0)

        extract_audio(video, output)
        assert output.parent.exists()

    @patch("src.ffmpeg.find_ffmpeg")
    @patch("subprocess.run")
    def test_raises_on_ffmpeg_error(self, mock_run, mock_ffmpeg, tmp_path):
        """Test error handling when ffmpeg fails."""
        from src.ffmpeg import extract_audio

        video = tmp_path / "test.mp4"
        video.write_bytes(b"fake")
        output = tmp_path / "audio.mp3"

        mock_ffmpeg.return_value = "ffmpeg"
        mock_run.return_value = MagicMock(returncode=1, stderr="Error message")

        with pytest.raises(RuntimeError):
            extract_audio(video, output)

    @patch("src.ffmpeg.find_ffmpeg")
    @patch("subprocess.run")
    def test_default_output_name(self, mock_run, mock_ffmpeg, tmp_path):
        """Test default output filename when no output specified."""
        from src.ffmpeg import extract_audio

        video = tmp_path / "myvideo.mp4"
        video.write_bytes(b"fake")

        mock_ffmpeg.return_value = "ffmpeg"
        mock_run.return_value = MagicMock(returncode=0)

        result = extract_audio(video)
        assert result.stem == "myvideo"
        assert result.suffix == ".mp3"


class TestGetVideoDuration:
    """Tests for get_video_duration function."""

    def test_video_not_found(self):
        """Test error for missing video file."""
        from src.ffmpeg import get_video_duration
        with pytest.raises(FileNotFoundError):
            get_video_duration(Path("/nonexistent/video.mp4"))

    @patch("shutil.which")
    @patch("subprocess.run")
    @patch("src.ffmpeg.find_ffmpeg")
    def test_parses_duration_from_ffprobe(self, mock_ffmpeg, mock_run, mock_which, tmp_path):
        """Test parsing duration from ffprobe output."""
        from src.ffmpeg import get_video_duration

        video = tmp_path / "test.mp4"
        video.write_bytes(b"fake")

        mock_which.return_value = "ffprobe"
        mock_ffmpeg.return_value = "ffmpeg"
        mock_run.return_value = MagicMock(
            returncode=0,
            stdout="120.5\n"
        )

        result = get_video_duration(video)
        assert result == 120.5

    @patch("shutil.which")
    @patch("subprocess.run")
    @patch("src.ffmpeg.find_ffmpeg")
    def test_parses_duration_from_ffmpeg_fallback(self, mock_ffmpeg, mock_run, mock_which, tmp_path):
        """Test parsing duration from ffmpeg stderr when ffprobe not available."""
        from src.ffmpeg import get_video_duration

        video = tmp_path / "test.mp4"
        video.write_bytes(b"fake")

        mock_which.return_value = None  # No ffprobe
        mock_ffmpeg.return_value = "ffmpeg"
        mock_run.return_value = MagicMock(
            returncode=0,
            stderr="Duration: 00:02:30.50, start: 0.000000"
        )

        result = get_video_duration(video)
        assert result == 150.5  # 2 min 30.5 sec


class TestGetVideoInfo:
    """Tests for get_video_info function."""

    def test_video_not_found(self):
        """Test error for missing video file."""
        from src.ffmpeg import get_video_info
        with pytest.raises(FileNotFoundError):
            get_video_info(Path("/nonexistent/video.mp4"))

    @patch("src.ffmpeg.get_video_duration")
    def test_returns_info_dict(self, mock_dur, tmp_path):
        """Test that info dict is returned."""
        from src.ffmpeg import get_video_info

        video = tmp_path / "test.mp4"
        video.write_bytes(b"fake video content here")

        mock_dur.return_value = 60.0

        result = get_video_info(video)

        assert result["name"] == "test.mp4"
        assert result["duration"] == 60.0
        assert result["size_bytes"] > 0
        assert result["format"] == "mp4"
