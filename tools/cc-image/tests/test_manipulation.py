"""Tests for image manipulation functions."""

import pytest
from pathlib import Path
from PIL import Image

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from src.manipulation import image_info, resize, convert


@pytest.fixture
def sample_image(tmp_path):
    """Create a sample test image."""
    img_path = tmp_path / "test.png"
    img = Image.new("RGB", (800, 600), color="red")
    img.save(img_path)
    return img_path


@pytest.fixture
def rgba_image(tmp_path):
    """Create a sample RGBA test image."""
    img_path = tmp_path / "test_rgba.png"
    img = Image.new("RGBA", (400, 300), color=(255, 0, 0, 128))
    img.save(img_path)
    return img_path


class TestImageInfo:
    """Tests for image_info function."""

    def test_file_not_found(self):
        """Test error for missing file."""
        with pytest.raises(FileNotFoundError):
            image_info(Path("/nonexistent/image.png"))

    def test_returns_correct_info(self, sample_image):
        """Test that correct info is returned."""
        info = image_info(sample_image)
        assert info["width"] == 800
        assert info["height"] == 600
        assert info["format"] == "PNG"
        assert info["mode"] == "RGB"
        assert info["size_bytes"] > 0

    def test_path_is_string(self, sample_image):
        """Test that path is returned as string."""
        info = image_info(sample_image)
        assert isinstance(info["path"], str)
        assert str(sample_image) == info["path"]


class TestResize:
    """Tests for resize function."""

    def test_file_not_found(self, tmp_path):
        """Test error for missing file."""
        with pytest.raises(FileNotFoundError):
            resize(Path("/nonexistent.png"), tmp_path / "out.png", width=100)

    def test_no_dimensions_raises(self, sample_image, tmp_path):
        """Test error when no dimensions specified."""
        with pytest.raises(ValueError) as exc:
            resize(sample_image, tmp_path / "out.png")
        assert "Must specify width or height" in str(exc.value)

    def test_resize_by_width(self, sample_image, tmp_path):
        """Test resizing by width, maintaining aspect ratio."""
        output = tmp_path / "resized.png"
        result = resize(sample_image, output, width=400)

        assert result.exists()
        with Image.open(result) as img:
            assert img.size[0] == 400
            assert img.size[1] == 300  # Aspect ratio maintained

    def test_resize_by_height(self, sample_image, tmp_path):
        """Test resizing by height, maintaining aspect ratio."""
        output = tmp_path / "resized.png"
        result = resize(sample_image, output, height=300)

        assert result.exists()
        with Image.open(result) as img:
            assert img.size[0] == 400  # Aspect ratio maintained
            assert img.size[1] == 300

    def test_resize_without_aspect_ratio(self, sample_image, tmp_path):
        """Test resizing without maintaining aspect ratio."""
        output = tmp_path / "resized.png"
        result = resize(sample_image, output, width=500, height=500, maintain_aspect=False)

        assert result.exists()
        with Image.open(result) as img:
            assert img.size == (500, 500)

    def test_creates_output_directory(self, sample_image, tmp_path):
        """Test that output directory is created."""
        output = tmp_path / "subdir" / "nested" / "resized.png"
        result = resize(sample_image, output, width=200)
        assert result.exists()
        assert result.parent.exists()


class TestConvert:
    """Tests for convert function."""

    def test_file_not_found(self, tmp_path):
        """Test error for missing file."""
        with pytest.raises(FileNotFoundError):
            convert(Path("/nonexistent.png"), tmp_path / "out.jpg")

    def test_png_to_jpeg(self, sample_image, tmp_path):
        """Test converting PNG to JPEG."""
        output = tmp_path / "converted.jpg"
        result = convert(sample_image, output)

        assert result.exists()
        with Image.open(result) as img:
            assert img.format == "JPEG"

    def test_rgba_to_jpeg(self, rgba_image, tmp_path):
        """Test converting RGBA to JPEG (handles transparency)."""
        output = tmp_path / "converted.jpg"
        result = convert(rgba_image, output)

        assert result.exists()
        with Image.open(result) as img:
            assert img.format == "JPEG"
            assert img.mode == "RGB"

    def test_jpeg_to_png(self, sample_image, tmp_path):
        """Test converting to PNG."""
        # First create a JPEG
        jpeg = tmp_path / "test.jpg"
        with Image.open(sample_image) as img:
            img.save(jpeg, "JPEG")

        output = tmp_path / "converted.png"
        result = convert(jpeg, output)

        assert result.exists()
        with Image.open(result) as img:
            assert img.format == "PNG"

    def test_to_webp(self, sample_image, tmp_path):
        """Test converting to WebP."""
        output = tmp_path / "converted.webp"
        result = convert(sample_image, output)

        assert result.exists()
        with Image.open(result) as img:
            assert img.format == "WEBP"
