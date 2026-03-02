"""Tests for vision functions (describe, OCR)."""

import pytest
from pathlib import Path
from unittest.mock import patch, MagicMock

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from src.vision import describe, extract_text


class TestDescribe:
    """Tests for describe function."""

    def test_file_not_found(self):
        """Test error for missing file."""
        with pytest.raises(FileNotFoundError):
            describe(Path("/nonexistent.png"))

    @patch("src.vision.get_llm_provider")
    def test_calls_provider(self, mock_get_provider, tmp_path):
        """Test that describe calls LLM provider correctly."""
        from PIL import Image

        img_path = tmp_path / "test.png"
        img = Image.new("RGB", (100, 100))
        img.save(img_path)

        mock_provider = MagicMock()
        mock_provider.describe_image.return_value = "A test image"
        mock_get_provider.return_value = mock_provider

        result = describe(img_path)

        assert result == "A test image"
        mock_get_provider.assert_called_once_with("claude_code")
        mock_provider.describe_image.assert_called_once_with(img_path)

    @patch("src.vision.get_llm_provider")
    def test_uses_specified_engine(self, mock_get_provider, tmp_path):
        """Test that engine parameter is passed to provider."""
        from PIL import Image

        img_path = tmp_path / "test.png"
        img = Image.new("RGB", (100, 100))
        img.save(img_path)

        mock_provider = MagicMock()
        mock_provider.describe_image.return_value = "Description"
        mock_get_provider.return_value = mock_provider

        describe(img_path, engine="openai")

        mock_get_provider.assert_called_once_with("openai")


class TestExtractText:
    """Tests for extract_text (OCR) function."""

    def test_file_not_found(self):
        """Test error for missing file."""
        with pytest.raises(FileNotFoundError):
            extract_text(Path("/nonexistent.png"))

    @patch("src.vision.get_llm_provider")
    def test_calls_provider(self, mock_get_provider, tmp_path):
        """Test that extract_text calls LLM provider correctly."""
        from PIL import Image

        img_path = tmp_path / "test.png"
        img = Image.new("RGB", (100, 100))
        img.save(img_path)

        mock_provider = MagicMock()
        mock_provider.extract_text.return_value = "Sample text"
        mock_get_provider.return_value = mock_provider

        result = extract_text(img_path)

        assert result == "Sample text"
        mock_get_provider.assert_called_once_with("claude_code")
        mock_provider.extract_text.assert_called_once_with(img_path)

    @patch("src.vision.get_llm_provider")
    def test_uses_specified_engine(self, mock_get_provider, tmp_path):
        """Test that engine parameter is passed to provider."""
        from PIL import Image

        img_path = tmp_path / "test.png"
        img = Image.new("RGB", (100, 100))
        img.save(img_path)

        mock_provider = MagicMock()
        mock_provider.extract_text.return_value = "Text"
        mock_get_provider.return_value = mock_provider

        extract_text(img_path, engine="openai")

        mock_get_provider.assert_called_once_with("openai")
