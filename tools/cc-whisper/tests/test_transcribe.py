"""Tests for Whisper transcription functions."""

import pytest
from pathlib import Path
from unittest.mock import patch, MagicMock, mock_open

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from src.transcribe import get_api_key


class TestGetApiKey:
    """Tests for get_api_key function."""

    @patch.dict("os.environ", {"OPENAI_API_KEY": "test-key-whisper"})
    def test_returns_key_from_env(self):
        """Test that API key is returned from environment."""
        key = get_api_key()
        assert key == "test-key-whisper"

    @patch.dict("os.environ", {}, clear=True)
    def test_raises_without_key(self):
        """Test error when no API key set."""
        import os
        os.environ.pop("OPENAI_API_KEY", None)
        with pytest.raises(RuntimeError) as exc:
            get_api_key()
        assert "OPENAI_API_KEY" in str(exc.value)


class TestTranscribe:
    """Tests for transcribe function."""

    def test_file_not_found(self):
        """Test error for missing audio file."""
        from src.transcribe import transcribe
        with pytest.raises(FileNotFoundError):
            transcribe(Path("/nonexistent/audio.mp3"))

    @patch("src.transcribe.get_api_key")
    @patch("src.transcribe.OpenAI")
    def test_returns_text(self, mock_openai, mock_key, tmp_path):
        """Test that transcription text is returned."""
        from src.transcribe import transcribe

        audio_file = tmp_path / "test.mp3"
        audio_file.write_bytes(b"fake audio data")

        mock_key.return_value = "test-key"
        mock_client = MagicMock()
        mock_openai.return_value = mock_client
        mock_client.audio.transcriptions.create.return_value = MagicMock(
            text="Hello, this is a test transcription."
        )

        result = transcribe(audio_file)

        assert "text" in result
        assert result["text"] == "Hello, this is a test transcription."

    @patch("src.transcribe.get_api_key")
    @patch("src.transcribe.OpenAI")
    def test_passes_language_parameter(self, mock_openai, mock_key, tmp_path):
        """Test that language parameter is passed to API."""
        from src.transcribe import transcribe

        audio_file = tmp_path / "test.mp3"
        audio_file.write_bytes(b"fake audio")

        mock_key.return_value = "test-key"
        mock_client = MagicMock()
        mock_openai.return_value = mock_client
        mock_client.audio.transcriptions.create.return_value = MagicMock(
            text="Texto en espanol"
        )

        transcribe(audio_file, language="es")

        call_kwargs = mock_client.audio.transcriptions.create.call_args.kwargs
        assert call_kwargs.get("language") == "es"

    @patch("src.transcribe.get_api_key")
    @patch("src.transcribe.OpenAI")
    def test_timestamps_mode(self, mock_openai, mock_key, tmp_path):
        """Test that timestamps mode returns segments."""
        from src.transcribe import transcribe

        audio_file = tmp_path / "test.mp3"
        audio_file.write_bytes(b"fake audio")

        mock_key.return_value = "test-key"
        mock_client = MagicMock()
        mock_openai.return_value = mock_client

        # Mock response with segments
        mock_segment = MagicMock()
        mock_segment.start = 0.0
        mock_segment.end = 5.0
        mock_segment.text = "First segment"

        mock_word = MagicMock()
        mock_word.word = "First"
        mock_word.start = 0.0
        mock_word.end = 0.5

        mock_response = MagicMock()
        mock_response.text = "Full transcription text"
        mock_response.segments = [mock_segment]
        mock_response.words = [mock_word]
        mock_response.duration = 5.0

        mock_client.audio.transcriptions.create.return_value = mock_response

        result = transcribe(audio_file, timestamps=True)

        assert "text" in result
        assert "segments" in result
        assert "words" in result
        assert len(result["segments"]) == 1
        assert result["segments"][0]["text"] == "First segment"


class TestTranscribeToFile:
    """Tests for transcribe_to_file function."""

    @patch("src.transcribe.transcribe")
    def test_saves_plain_text(self, mock_transcribe, tmp_path):
        """Test that plain text is saved to file."""
        from src.transcribe import transcribe_to_file

        mock_transcribe.return_value = {"text": "Hello world"}
        audio = tmp_path / "test.mp3"
        audio.write_bytes(b"fake")
        output = tmp_path / "transcript.txt"

        result = transcribe_to_file(audio, output)

        assert result == output
        assert output.exists()
        assert output.read_text() == "Hello world"

    @patch("src.transcribe.transcribe")
    def test_saves_with_timestamps(self, mock_transcribe, tmp_path):
        """Test that timestamps are saved correctly."""
        from src.transcribe import transcribe_to_file

        mock_transcribe.return_value = {
            "text": "Full text",
            "segments": [
                {"start": 0, "end": 5, "text": "First part"},
                {"start": 5, "end": 10, "text": "Second part"},
            ]
        }
        audio = tmp_path / "test.mp3"
        audio.write_bytes(b"fake")
        output = tmp_path / "transcript.txt"

        result = transcribe_to_file(audio, output, timestamps=True)

        content = output.read_text()
        assert "[00:00]" in content
        assert "[00:05]" in content
        assert "First part" in content
        assert "Second part" in content

    @patch("src.transcribe.transcribe")
    def test_creates_output_directory(self, mock_transcribe, tmp_path):
        """Test that output directory is created."""
        from src.transcribe import transcribe_to_file

        mock_transcribe.return_value = {"text": "Test"}
        audio = tmp_path / "test.mp3"
        audio.write_bytes(b"fake")
        output = tmp_path / "subdir" / "transcript.txt"

        result = transcribe_to_file(audio, output)

        assert output.parent.exists()
        assert result.exists()
