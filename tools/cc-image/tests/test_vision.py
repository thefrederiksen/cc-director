"""Tests for vision functions (describe, OCR)."""

import pytest
from pathlib import Path
from unittest.mock import patch, MagicMock

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from src.vision import get_api_key, encode_image, get_media_type


class TestGetApiKey:
    """Tests for get_api_key function."""

    @patch.dict("os.environ", {"OPENAI_API_KEY": "test-key-123"})
    def test_returns_key_from_env(self):
        """Test that API key is returned from environment."""
        key = get_api_key()
        assert key == "test-key-123"

    @patch.dict("os.environ", {}, clear=True)
    def test_raises_without_key(self):
        """Test error when no API key set."""
        import os
        os.environ.pop("OPENAI_API_KEY", None)
        with pytest.raises(RuntimeError) as exc:
            get_api_key()
        assert "OPENAI_API_KEY" in str(exc.value)


class TestEncodeImage:
    """Tests for encode_image function."""

    def test_returns_base64(self, tmp_path):
        """Test that base64 string is returned."""
        from PIL import Image

        img_path = tmp_path / "test.png"
        img = Image.new("RGB", (100, 100), color="blue")
        img.save(img_path)

        result = encode_image(img_path)
        assert isinstance(result, str)
        assert len(result) > 0
        # Base64 should only contain valid characters
        import base64
        try:
            base64.b64decode(result)
        except Exception:
            pytest.fail("Result is not valid base64")


class TestGetMediaType:
    """Tests for get_media_type function."""

    def test_jpeg(self, tmp_path):
        """Test JPEG media type."""
        assert get_media_type(Path("test.jpg")) == "image/jpeg"
        assert get_media_type(Path("test.jpeg")) == "image/jpeg"

    def test_png(self, tmp_path):
        """Test PNG media type."""
        assert get_media_type(Path("test.png")) == "image/png"

    def test_gif(self):
        """Test GIF media type."""
        assert get_media_type(Path("test.gif")) == "image/gif"

    def test_webp(self):
        """Test WebP media type."""
        assert get_media_type(Path("test.webp")) == "image/webp"

    def test_unknown_defaults_to_jpeg(self):
        """Test unknown extension defaults to JPEG."""
        assert get_media_type(Path("test.bmp")) == "image/jpeg"


class TestVision:
    """Tests for vision function."""

    def test_file_not_found(self):
        """Test error for missing file."""
        from src.vision import vision
        with pytest.raises(FileNotFoundError):
            vision(Path("/nonexistent.png"), "Describe this")

    @patch("src.vision.get_api_key")
    @patch("src.vision.requests.post")
    def test_calls_api_correctly(self, mock_post, mock_key, tmp_path):
        """Test that API is called with correct parameters."""
        from PIL import Image
        from src.vision import vision

        img_path = tmp_path / "test.png"
        img = Image.new("RGB", (100, 100), color="green")
        img.save(img_path)

        mock_key.return_value = "test-key"
        mock_post.return_value = MagicMock(
            status_code=200,
            json=lambda: {"choices": [{"message": {"content": "A green square"}}]}
        )

        result = vision(img_path, "Describe this image")
        assert result == "A green square"
        mock_post.assert_called_once()

    @patch("src.vision.get_api_key")
    @patch("src.vision.requests.post")
    def test_raises_on_api_error(self, mock_post, mock_key, tmp_path):
        """Test error handling for API errors."""
        from PIL import Image
        from src.vision import vision

        img_path = tmp_path / "test.png"
        img = Image.new("RGB", (100, 100), color="red")
        img.save(img_path)

        mock_key.return_value = "test-key"
        mock_post.return_value = MagicMock(
            status_code=400,
            text="Bad Request"
        )

        with pytest.raises(RuntimeError) as exc:
            vision(img_path, "Describe")
        assert "Vision API error" in str(exc.value)


class TestDescribe:
    """Tests for describe function."""

    def test_file_not_found(self):
        """Test error for missing file."""
        from src.vision import describe
        with pytest.raises(FileNotFoundError):
            describe(Path("/nonexistent.png"))

    @patch("src.vision.vision")
    def test_calls_vision(self, mock_vision, tmp_path):
        """Test that describe calls vision correctly."""
        from PIL import Image
        from src.vision import describe

        img_path = tmp_path / "test.png"
        img = Image.new("RGB", (100, 100))
        img.save(img_path)

        mock_vision.return_value = "A test image"
        result = describe(img_path)

        assert result == "A test image"
        mock_vision.assert_called_once()
        call_args = mock_vision.call_args
        assert "Describe" in call_args[0][1]


class TestExtractText:
    """Tests for extract_text (OCR) function."""

    def test_file_not_found(self):
        """Test error for missing file."""
        from src.vision import extract_text
        with pytest.raises(FileNotFoundError):
            extract_text(Path("/nonexistent.png"))

    @patch("src.vision.vision")
    def test_calls_vision(self, mock_vision, tmp_path):
        """Test that extract_text calls vision correctly."""
        from PIL import Image
        from src.vision import extract_text

        img_path = tmp_path / "test.png"
        img = Image.new("RGB", (100, 100))
        img.save(img_path)

        mock_vision.return_value = "Sample text"
        result = extract_text(img_path)

        assert result == "Sample text"
        mock_vision.assert_called_once()
        call_args = mock_vision.call_args
        assert "Extract" in call_args[0][1] or "text" in call_args[0][1].lower()
