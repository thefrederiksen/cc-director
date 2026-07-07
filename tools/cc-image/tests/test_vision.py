"""Tests for vision functions (describe, OCR)."""

import pytest
from pathlib import Path
from unittest.mock import patch, MagicMock

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from src.vision import describe, extract_text, DEFAULT_ENGINE


def _make_image(tmp_path):
    from PIL import Image

    img_path = tmp_path / "test.png"
    Image.new("RGB", (100, 100)).save(img_path)
    return img_path


class TestDescribe:
    """Tests for describe function."""

    def test_file_not_found(self):
        with pytest.raises(FileNotFoundError):
            describe(Path("/nonexistent.png"))

    def test_default_engine_is_devthrottle(self):
        assert DEFAULT_ENGINE == "devthrottle"

    @patch("src.vision._get_provider")
    def test_calls_provider_with_default_engine(self, mock_get_provider, tmp_path):
        img_path = _make_image(tmp_path)

        mock_provider = MagicMock()
        mock_provider.describe_image.return_value = "A test image"
        mock_get_provider.return_value = mock_provider

        result = describe(img_path)

        assert result == "A test image"
        mock_get_provider.assert_called_once_with("devthrottle")
        mock_provider.describe_image.assert_called_once_with(img_path)

    @patch("src.vision._get_provider")
    def test_uses_specified_engine(self, mock_get_provider, tmp_path):
        img_path = _make_image(tmp_path)

        mock_provider = MagicMock()
        mock_provider.describe_image.return_value = "Description"
        mock_get_provider.return_value = mock_provider

        describe(img_path, engine="openai")

        mock_get_provider.assert_called_once_with("openai")


class TestExtractText:
    """Tests for extract_text (OCR) function."""

    def test_file_not_found(self):
        with pytest.raises(FileNotFoundError):
            extract_text(Path("/nonexistent.png"))

    @patch("src.vision._get_provider")
    def test_calls_provider_with_default_engine(self, mock_get_provider, tmp_path):
        img_path = _make_image(tmp_path)

        mock_provider = MagicMock()
        mock_provider.extract_text.return_value = "Sample text"
        mock_get_provider.return_value = mock_provider

        result = extract_text(img_path)

        assert result == "Sample text"
        mock_get_provider.assert_called_once_with("devthrottle")
        mock_provider.extract_text.assert_called_once_with(img_path)

    @patch("src.vision._get_provider")
    def test_uses_specified_engine(self, mock_get_provider, tmp_path):
        img_path = _make_image(tmp_path)

        mock_provider = MagicMock()
        mock_provider.extract_text.return_value = "Text"
        mock_get_provider.return_value = mock_provider

        extract_text(img_path, engine="claude_code")

        mock_get_provider.assert_called_once_with("claude_code")


class TestProviderRouting:
    """The devthrottle engine routes to the DevThrottle client; others go to cc_shared."""

    @patch("src.vision.DevThrottleVisionClient")
    def test_devthrottle_engine_uses_devthrottle_client(self, mock_client, tmp_path):
        from src.vision import _get_provider

        _get_provider("devthrottle")
        mock_client.assert_called_once()

    @patch("src.vision._get_shared_provider")
    def test_other_engine_uses_shared_provider(self, mock_shared):
        from src.vision import _get_provider

        _get_provider("openai")
        mock_shared.assert_called_once_with("openai")
