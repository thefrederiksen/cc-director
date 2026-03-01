"""Tests for image generation functions."""

import pytest
from pathlib import Path
from unittest.mock import patch, MagicMock

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from src.generation import get_api_key


class TestGetApiKey:
    """Tests for get_api_key function."""

    @patch.dict("os.environ", {"OPENAI_API_KEY": "test-key-456"})
    def test_returns_key_from_env(self):
        """Test that API key is returned from environment."""
        key = get_api_key()
        assert key == "test-key-456"

    @patch.dict("os.environ", {}, clear=True)
    def test_raises_without_key(self):
        """Test error when no API key set."""
        import os
        os.environ.pop("OPENAI_API_KEY", None)
        with pytest.raises(RuntimeError) as exc:
            get_api_key()
        assert "OPENAI_API_KEY" in str(exc.value)


class TestGenerate:
    """Tests for generate function."""

    @patch("src.generation.get_api_key")
    @patch("src.generation.requests.post")
    @patch("src.generation.requests.get")
    def test_returns_image_bytes(self, mock_get, mock_post, mock_key):
        """Test that image bytes are returned."""
        from src.generation import generate
        from PIL import Image
        import io

        mock_key.return_value = "test-key"
        mock_post.return_value = MagicMock(
            status_code=200,
            json=lambda: {"data": [{"url": "https://example.com/image.png"}]}
        )

        buf = io.BytesIO()
        Image.new("RGB", (100, 100), "blue").save(buf, format="PNG")
        mock_get.return_value = MagicMock(
            status_code=200,
            content=buf.getvalue()
        )

        result = generate("A blue square")
        assert isinstance(result, bytes)
        assert len(result) > 0

    @patch("src.generation.get_api_key")
    @patch("src.generation.requests.post")
    def test_raises_on_api_error(self, mock_post, mock_key):
        """Test error handling for API errors."""
        from src.generation import generate

        mock_key.return_value = "test-key"
        mock_post.return_value = MagicMock(
            status_code=400,
            text="Bad Request"
        )

        with pytest.raises(RuntimeError) as exc:
            generate("A test prompt")
        assert "DALL-E error" in str(exc.value)

    @patch("src.generation.get_api_key")
    @patch("src.generation.requests.post")
    def test_raises_on_empty_data(self, mock_post, mock_key):
        """Test error handling when no image generated."""
        from src.generation import generate

        mock_key.return_value = "test-key"
        mock_post.return_value = MagicMock(
            status_code=200,
            json=lambda: {"data": []}
        )

        with pytest.raises(RuntimeError) as exc:
            generate("A test prompt")
        assert "No image generated" in str(exc.value)

    @patch("src.generation.get_api_key")
    @patch("src.generation.requests.post")
    @patch("src.generation.requests.get")
    def test_raises_on_download_error(self, mock_get, mock_post, mock_key):
        """Test error handling when download fails."""
        from src.generation import generate

        mock_key.return_value = "test-key"
        mock_post.return_value = MagicMock(
            status_code=200,
            json=lambda: {"data": [{"url": "https://example.com/image.png"}]}
        )
        mock_get.return_value = MagicMock(status_code=404)

        with pytest.raises(RuntimeError) as exc:
            generate("A test prompt")
        assert "Failed to download" in str(exc.value)


class TestGenerateToFile:
    """Tests for generate_to_file function."""

    @patch("src.generation.generate")
    def test_saves_to_file(self, mock_generate, tmp_path):
        """Test that image is saved to file."""
        from src.generation import generate_to_file
        from PIL import Image
        import io

        buf = io.BytesIO()
        Image.new("RGB", (100, 100), "red").save(buf, format="PNG")
        mock_generate.return_value = buf.getvalue()

        output = tmp_path / "generated.png"
        result = generate_to_file("A red square", output)

        assert result == output
        assert output.exists()
        assert len(output.read_bytes()) > 0

    @patch("src.generation.generate")
    def test_creates_output_directory(self, mock_generate, tmp_path):
        """Test that output directory is created."""
        from src.generation import generate_to_file
        from PIL import Image
        import io

        buf = io.BytesIO()
        Image.new("RGB", (50, 50), "green").save(buf, format="PNG")
        mock_generate.return_value = buf.getvalue()

        output = tmp_path / "subdir" / "nested" / "generated.png"
        result = generate_to_file("A green square", output)

        assert output.parent.exists()
        assert result.exists()

    @patch("src.generation.generate")
    def test_passes_parameters(self, mock_generate, tmp_path):
        """Test that parameters are passed correctly."""
        from src.generation import generate_to_file

        mock_generate.return_value = b"fake image data"

        output = tmp_path / "generated.png"
        generate_to_file("Test prompt", output, size="1024x1792", quality="hd")

        mock_generate.assert_called_once_with("Test prompt", "1024x1792", "hd")
