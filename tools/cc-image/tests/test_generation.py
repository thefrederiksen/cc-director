"""Tests for image generation (deferred DevThrottle images endpoint)."""

import pytest
from pathlib import Path
from unittest.mock import patch, MagicMock

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from src.generation import generate, generate_to_file, GenerationNotAvailableError


class TestDeferred:
    """While the DevThrottle images endpoint is not built, generation refuses clearly."""

    def test_generate_raises_not_available(self):
        with pytest.raises(GenerationNotAvailableError) as exc:
            generate("A blue square")
        assert "not built yet" in str(exc.value)

    def test_generate_to_file_raises_not_available(self, tmp_path):
        with pytest.raises(GenerationNotAvailableError):
            generate_to_file("A blue square", tmp_path / "out.png")

    def test_does_not_require_openai_key(self):
        # No OPENAI_API_KEY involvement at all - the deferred error is raised up front.
        with patch.dict("os.environ", {}, clear=True):
            with pytest.raises(GenerationNotAvailableError):
                generate("anything")


class TestWiredEndpoint:
    """When the endpoint is flagged available, generate() posts to DevThrottle images."""

    @patch("src.generation.IMAGES_ENDPOINT_AVAILABLE", True)
    @patch("src.generation.get_api_key", return_value="dt_test")
    @patch("src.generation.get_api_base", return_value="https://devthrottle.com/api/v1")
    @patch("src.generation.requests.post")
    def test_posts_to_devthrottle_images_endpoint(self, mock_post, _base, _key):
        import base64
        from PIL import Image
        import io

        buf = io.BytesIO()
        Image.new("RGB", (10, 10), "blue").save(buf, format="PNG")
        b64 = base64.b64encode(buf.getvalue()).decode("ascii")

        mock_post.return_value = MagicMock(
            status_code=200,
            json=lambda: {"data": [{"b64_json": b64}]},
        )

        result = generate("A blue square")

        assert isinstance(result, bytes) and len(result) > 0
        url = mock_post.call_args[0][0]
        assert url == "https://devthrottle.com/api/v1/images/generations"
        headers = mock_post.call_args[1]["headers"]
        assert headers["Authorization"] == "Bearer dt_test"

    @patch("src.generation.IMAGES_ENDPOINT_AVAILABLE", True)
    @patch("src.generation.get_api_key", return_value="dt_test")
    @patch("src.generation.get_api_base", return_value="https://devthrottle.com/api/v1")
    @patch("src.generation.requests.post")
    def test_raises_on_api_error(self, mock_post, _base, _key):
        mock_post.return_value = MagicMock(status_code=400, text="Bad Request")
        with pytest.raises(RuntimeError) as exc:
            generate("A test prompt")
        assert "DevThrottle images error" in str(exc.value)
