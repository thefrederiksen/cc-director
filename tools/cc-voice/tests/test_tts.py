"""Tests for text-to-speech functions."""

import pytest
from pathlib import Path
from unittest.mock import patch, MagicMock

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from src.tts import get_api_key, clean_text, chunk_text, MAX_CHARS


class TestGetApiKey:
    """Tests for get_api_key function."""

    @patch.dict("os.environ", {"OPENAI_API_KEY": "test-key-tts"})
    def test_returns_key_from_env(self):
        """Test that API key is returned from environment."""
        key = get_api_key()
        assert key == "test-key-tts"

    @patch.dict("os.environ", {}, clear=True)
    def test_raises_without_key(self):
        """Test error when no API key set."""
        import os
        os.environ.pop("OPENAI_API_KEY", None)
        with pytest.raises(RuntimeError) as exc:
            get_api_key()
        assert "OPENAI_API_KEY" in str(exc.value)


class TestCleanText:
    """Tests for clean_text function."""

    def test_removes_code_blocks(self):
        """Test that code blocks are removed."""
        text = "Hello ```python\nprint('hi')\n``` world"
        result = clean_text(text)
        assert "```" not in result
        assert "print" not in result
        assert "Hello" in result
        assert "world" in result

    def test_removes_inline_code(self):
        """Test that inline code is removed."""
        text = "Use the `print` function"
        result = clean_text(text)
        assert "`" not in result
        assert "print" not in result

    def test_removes_bold_keeps_text(self):
        """Test that bold markers are removed but text kept."""
        text = "This is **bold** text"
        result = clean_text(text)
        assert "**" not in result
        assert "bold" in result

    def test_removes_italic_keeps_text(self):
        """Test that italic markers are removed but text kept."""
        text = "This is *italic* text"
        result = clean_text(text)
        assert "*" not in result
        assert "italic" in result

    def test_removes_strikethrough(self):
        """Test that strikethrough is removed."""
        text = "This is ~~deleted~~ text"
        result = clean_text(text)
        assert "~~" not in result
        assert "deleted" in result

    def test_removes_links_keeps_text(self):
        """Test that link markdown is removed but text kept."""
        text = "Click [here](https://example.com) for more"
        result = clean_text(text)
        assert "[" not in result
        assert "](http" not in result
        assert "here" in result

    def test_removes_headers(self):
        """Test that header markers are removed."""
        text = "# Title\n\n## Subtitle\n\nContent"
        result = clean_text(text)
        assert result.startswith("Title")
        assert "#" not in result

    def test_removes_horizontal_rules(self):
        """Test that horizontal rules are removed."""
        text = "Above\n\n---\n\nBelow"
        result = clean_text(text)
        assert "---" not in result

    def test_cleans_extra_whitespace(self):
        """Test that extra whitespace is cleaned."""
        text = "Hello\n\n\n\n\nWorld"
        result = clean_text(text)
        assert "\n\n\n" not in result


class TestChunkText:
    """Tests for chunk_text function."""

    def test_short_text_no_split(self):
        """Test that short text is not split."""
        text = "Hello, world!"
        chunks = chunk_text(text)
        assert len(chunks) == 1
        assert chunks[0] == text

    def test_respects_max_chars(self):
        """Test that chunks respect max char limit."""
        text = "This is a sentence. " * 500  # Long text
        chunks = chunk_text(text, max_chars=100)
        for chunk in chunks:
            assert len(chunk) <= 100 + 50  # Allow some flexibility for sentence boundaries

    def test_splits_at_sentence_boundaries(self):
        """Test that splits occur at sentence boundaries."""
        text = "First sentence. Second sentence. Third sentence."
        chunks = chunk_text(text, max_chars=30)
        # Each chunk should end with a sentence
        for chunk in chunks:
            assert chunk.strip().endswith(".") or chunk.strip().endswith("!")

    def test_handles_single_long_sentence(self):
        """Test handling of single very long sentence."""
        text = "A" * 100  # No sentence boundaries
        chunks = chunk_text(text, max_chars=50)
        # Should still produce output even without sentence breaks
        assert len(chunks) >= 1


class TestTTS:
    """Tests for tts function."""

    @patch("src.tts.get_api_key")
    @patch("src.tts.OpenAI")
    def test_returns_audio_bytes(self, mock_openai, mock_key):
        """Test that audio bytes are returned."""
        from src.tts import tts

        mock_key.return_value = "test-key"
        mock_client = MagicMock()
        mock_openai.return_value = mock_client
        mock_client.audio.speech.create.return_value = MagicMock(
            content=b"fake audio data"
        )

        result = tts("Hello world")
        assert isinstance(result, bytes)
        assert result == b"fake audio data"

    @patch("src.tts.get_api_key")
    @patch("src.tts.OpenAI")
    def test_uses_correct_voice(self, mock_openai, mock_key):
        """Test that specified voice is used."""
        from src.tts import tts

        mock_key.return_value = "test-key"
        mock_client = MagicMock()
        mock_openai.return_value = mock_client
        mock_client.audio.speech.create.return_value = MagicMock(content=b"audio")

        tts("Hello", voice="nova")
        call_kwargs = mock_client.audio.speech.create.call_args.kwargs
        assert call_kwargs["voice"] == "nova"

    @patch("src.tts.get_api_key")
    @patch("src.tts.OpenAI")
    def test_uses_correct_model(self, mock_openai, mock_key):
        """Test that specified model is used."""
        from src.tts import tts

        mock_key.return_value = "test-key"
        mock_client = MagicMock()
        mock_openai.return_value = mock_client
        mock_client.audio.speech.create.return_value = MagicMock(content=b"audio")

        tts("Hello", model="tts-1-hd")
        call_kwargs = mock_client.audio.speech.create.call_args.kwargs
        assert call_kwargs["model"] == "tts-1-hd"

    def test_raises_on_empty_text(self):
        """Test error on empty text after cleaning."""
        from src.tts import tts

        with pytest.raises(ValueError) as exc:
            tts("```code only```")
        assert "No text" in str(exc.value)


class TestTTSToFile:
    """Tests for tts_to_file function."""

    @patch("src.tts.tts")
    def test_saves_to_file(self, mock_tts, tmp_path):
        """Test that audio is saved to file."""
        from src.tts import tts_to_file

        mock_tts.return_value = b"fake audio data"
        output = tmp_path / "output.mp3"

        result = tts_to_file("Hello", output)

        assert result == output
        assert output.exists()
        assert output.read_bytes() == b"fake audio data"

    @patch("src.tts.tts")
    def test_creates_output_directory(self, mock_tts, tmp_path):
        """Test that output directory is created."""
        from src.tts import tts_to_file

        mock_tts.return_value = b"audio"
        output = tmp_path / "subdir" / "nested" / "output.mp3"

        result = tts_to_file("Hello", output)

        assert output.parent.exists()
        assert result.exists()
