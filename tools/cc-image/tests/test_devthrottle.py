"""Tests for the DevThrottle vision client."""

import base64
import io
import pytest
from pathlib import Path
from unittest.mock import patch, MagicMock

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from src.devthrottle import (
    DevThrottleVisionClient,
    DevThrottleConfigError,
    DevThrottleTransientError,
    encode_image_data_uri,
    get_api_base,
    get_vision_model,
    DEFAULT_API_BASE,
    DEFAULT_VISION_MODEL,
    _extract_content,
)


def _make_image(tmp_path, size=(2000, 1000), mode="RGB", color="red"):
    from PIL import Image

    img_path = tmp_path / f"test.png"
    Image.new(mode, size, color if mode == "RGB" else None).save(img_path)
    return img_path


class TestConfig:
    def test_api_key_required(self):
        with patch.dict("os.environ", {}, clear=True):
            with pytest.raises(DevThrottleConfigError) as exc:
                DevThrottleVisionClient()
            assert "DEVTHROTTLE_API_KEY" in str(exc.value)

    def test_default_base_and_model(self):
        with patch.dict("os.environ", {}, clear=True):
            assert get_api_base() == DEFAULT_API_BASE
            assert get_vision_model() == DEFAULT_VISION_MODEL

    def test_base_url_configurable(self):
        with patch.dict("os.environ", {"DEVTHROTTLE_API_BASE": "http://localhost:8080/api/v1/"}, clear=True):
            assert get_api_base() == "http://localhost:8080/api/v1"

    def test_model_configurable(self):
        with patch.dict("os.environ", {"DEVTHROTTLE_VISION_MODEL": "some/other-model"}, clear=True):
            assert get_vision_model() == "some/other-model"

    def test_chat_url(self):
        client = DevThrottleVisionClient(api_key="dt_test", api_base="https://x/api/v1")
        assert client.chat_url == "https://x/api/v1/chat/completions"


class TestEncodeImage:
    def test_downscales_long_edge(self, tmp_path):
        img_path = _make_image(tmp_path, size=(2000, 1000))
        uri = encode_image_data_uri(img_path, max_edge=896)
        assert uri.startswith("data:image/jpeg;base64,")

        raw = base64.b64decode(uri.split(",", 1)[1])
        from PIL import Image
        with Image.open(io.BytesIO(raw)) as out:
            assert max(out.size) == 896
            assert out.size == (896, 448)

    def test_no_upscale_for_small_image(self, tmp_path):
        img_path = _make_image(tmp_path, size=(200, 100))
        uri = encode_image_data_uri(img_path, max_edge=896)
        raw = base64.b64decode(uri.split(",", 1)[1])
        from PIL import Image
        with Image.open(io.BytesIO(raw)) as out:
            assert out.size == (200, 100)

    def test_flattens_transparency(self, tmp_path):
        from PIL import Image

        img_path = tmp_path / "alpha.png"
        Image.new("RGBA", (300, 300), (255, 0, 0, 0)).save(img_path)
        uri = encode_image_data_uri(img_path)
        raw = base64.b64decode(uri.split(",", 1)[1])
        with Image.open(io.BytesIO(raw)) as out:
            assert out.mode == "RGB"


class TestExtractContent:
    def test_extracts_message_content(self):
        body = {"choices": [{"message": {"content": "  hello world  "}}]}
        assert _extract_content(body) == "hello world"

    def test_no_choices_raises(self):
        with pytest.raises(RuntimeError):
            _extract_content({"choices": []})

    def test_empty_content_raises(self):
        with pytest.raises(RuntimeError):
            _extract_content({"choices": [{"message": {"content": "  "}}]})


class TestVisionCall:
    @patch("src.devthrottle.requests.post")
    def test_describe_posts_expected_payload(self, mock_post, tmp_path):
        img_path = _make_image(tmp_path, size=(400, 400))
        mock_post.return_value = MagicMock(
            status_code=200,
            json=lambda: {"choices": [{"message": {"content": "a red square"}}]},
        )

        client = DevThrottleVisionClient(api_key="dt_test", api_base="https://devthrottle.com/api/v1", model="google/gemma-3-27b-it")
        result = client.describe_image(img_path)

        assert result == "a red square"
        url = mock_post.call_args[0][0]
        assert url == "https://devthrottle.com/api/v1/chat/completions"

        kwargs = mock_post.call_args[1]
        assert kwargs["headers"]["Authorization"] == "Bearer dt_test"
        payload = kwargs["json"]
        assert payload["model"] == "google/gemma-3-27b-it"
        content = payload["messages"][0]["content"]
        assert content[0]["type"] == "text"
        assert content[1]["type"] == "image_url"
        assert content[1]["image_url"]["url"].startswith("data:image/jpeg;base64,")

    @patch("src.devthrottle.requests.post")
    def test_ocr_uses_ocr_prompt(self, mock_post, tmp_path):
        img_path = _make_image(tmp_path, size=(400, 400))
        mock_post.return_value = MagicMock(
            status_code=200,
            json=lambda: {"choices": [{"message": {"content": "TEXT"}}]},
        )
        client = DevThrottleVisionClient(api_key="dt_test")
        client.extract_text(img_path)

        prompt = mock_post.call_args[1]["json"]["messages"][0]["content"][0]["text"]
        assert "Extract all text" in prompt

    @patch("src.devthrottle.requests.post")
    def test_rate_limit_raises_transient(self, mock_post, tmp_path):
        img_path = _make_image(tmp_path, size=(400, 400))
        mock_post.return_value = MagicMock(status_code=429, reason="Too Many Requests", text="slow down")
        client = DevThrottleVisionClient(api_key="dt_test")
        with pytest.raises(DevThrottleTransientError):
            client.describe_image(img_path)

    @patch("src.devthrottle.requests.post")
    def test_non_retriable_raises_runtime(self, mock_post, tmp_path):
        img_path = _make_image(tmp_path, size=(400, 400))
        mock_post.return_value = MagicMock(status_code=401, reason="Unauthorized", text="bad key")
        client = DevThrottleVisionClient(api_key="dt_test")
        with pytest.raises(RuntimeError) as exc:
            client.describe_image(img_path)
        assert not isinstance(exc.value, DevThrottleTransientError)

    def test_missing_file_raises(self, tmp_path):
        client = DevThrottleVisionClient(api_key="dt_test")
        with pytest.raises(FileNotFoundError):
            client.describe_image(tmp_path / "nope.png")
