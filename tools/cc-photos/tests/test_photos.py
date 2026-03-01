"""Unit tests for cc-photos modules.

Tests cover: hasher, metadata, screenshot, duplicates, scanner, analyzer.
No API calls, no database, no cc-vault. Uses tmp_path for file system tests.
"""

import importlib
import sys
import types
from datetime import datetime
from pathlib import Path
from unittest.mock import MagicMock

import pytest

# ---------------------------------------------------------------------------
# Module-level setup: make src/ importable and stub out dependencies
# that use relative imports to database, cc_shared, etc.
# ---------------------------------------------------------------------------

_src_dir = str(Path(__file__).parent.parent / "src")
if _src_dir not in sys.path:
    sys.path.insert(0, _src_dir)

# Create a fake parent package so relative imports resolve correctly.
# The src/ modules use "from . import database" and "from .metadata import ...".
# We create a package named "cc_photos_src" and register each module under it.
_PKG = "cc_photos_src"
_pkg_mod = types.ModuleType(_PKG)
_pkg_mod.__path__ = [_src_dir]
_pkg_mod.__package__ = _PKG
sys.modules[_PKG] = _pkg_mod

# Stub out the database module so scanner/duplicates/analyzer can import it.
_db_stub = types.ModuleType(f"{_PKG}.database")
_db_stub.__package__ = _PKG
sys.modules[f"{_PKG}.database"] = _db_stub
# Make "from . import database" resolve from within the package
_pkg_mod.database = _db_stub

# Stub cc_shared so analyzer does not fail on import
_cc_shared_stub = types.ModuleType("cc_shared")
_cc_shared_stub.get_llm_provider = None
_cc_shared_stub.LLMProvider = None
sys.modules["cc_shared"] = _cc_shared_stub


def _import_from_pkg(module_name):
    """Import a module from the cc-photos src/ directory as a sub-module of
    the fake parent package, so that relative imports work."""
    full_name = f"{_PKG}.{module_name}"
    if full_name in sys.modules:
        return sys.modules[full_name]
    spec = importlib.util.spec_from_file_location(
        full_name,
        Path(_src_dir) / f"{module_name}.py",
        submodule_search_locations=[],
    )
    mod = importlib.util.module_from_spec(spec)
    mod.__package__ = _PKG
    sys.modules[full_name] = mod
    # Also expose as attribute on the package so sibling imports work
    setattr(_pkg_mod, module_name, mod)
    spec.loader.exec_module(mod)
    return mod


# Pre-import modules that other modules depend on (order matters)
_metadata_mod = _import_from_pkg("metadata")
_hasher_mod = _import_from_pkg("hasher")
_screenshot_mod = _import_from_pkg("screenshot")
_duplicates_mod = _import_from_pkg("duplicates")
_scanner_mod = _import_from_pkg("scanner")
_analyzer_mod = _import_from_pkg("analyzer")


# ---------------------------------------------------------------------------
# TestHasher
# ---------------------------------------------------------------------------

class TestHasher:
    """Tests for hasher.compute_sha256 and compute_sha256_quick."""

    def test_compute_sha256_consistent_hash(self, tmp_path):
        """compute_sha256() produces the same hash for the same file content."""
        test_file = tmp_path / "photo.jpg"
        test_file.write_bytes(b"identical content for hashing test")

        hash1 = _hasher_mod.compute_sha256(test_file)
        hash2 = _hasher_mod.compute_sha256(test_file)

        assert hash1 == hash2
        assert isinstance(hash1, str)
        assert len(hash1) == 64  # SHA-256 hex digest is 64 chars

    def test_compute_sha256_different_files(self, tmp_path):
        """compute_sha256() produces different hashes for different file content."""
        file_a = tmp_path / "a.jpg"
        file_b = tmp_path / "b.jpg"
        file_a.write_bytes(b"content A")
        file_b.write_bytes(b"content B")

        hash_a = _hasher_mod.compute_sha256(file_a)
        hash_b = _hasher_mod.compute_sha256(file_b)

        assert hash_a != hash_b

    def test_compute_sha256_quick_returns_string(self, tmp_path):
        """compute_sha256_quick() returns a hex string."""
        test_file = tmp_path / "image.png"
        test_file.write_bytes(b"x" * 2048)

        result = _hasher_mod.compute_sha256_quick(test_file)

        assert isinstance(result, str)
        assert len(result) == 64

    def test_compute_sha256_file_not_found(self, tmp_path):
        """compute_sha256() raises an error when the file does not exist."""
        missing = tmp_path / "nonexistent.jpg"

        with pytest.raises((FileNotFoundError, OSError)):
            _hasher_mod.compute_sha256(missing)


# ---------------------------------------------------------------------------
# TestMetadata
# ---------------------------------------------------------------------------

class TestMetadata:
    """Tests for metadata helper functions."""

    def test_parse_exif_datetime_valid(self):
        """_parse_exif_datetime() parses a standard EXIF datetime string."""
        result = _metadata_mod._parse_exif_datetime("2024:01:15 10:30:00")

        assert result is not None
        assert isinstance(result, datetime)
        assert result.year == 2024
        assert result.month == 1
        assert result.day == 15
        assert result.hour == 10
        assert result.minute == 30
        assert result.second == 0

    def test_parse_exif_datetime_invalid(self):
        """_parse_exif_datetime() returns None for invalid strings."""
        assert _metadata_mod._parse_exif_datetime("not-a-date") is None
        assert _metadata_mod._parse_exif_datetime("") is None
        assert _metadata_mod._parse_exif_datetime("garbage:data") is None

    def test_convert_gps_to_decimal_north_east(self):
        """_convert_gps_to_decimal() returns positive value for N/E ref."""
        # 40 degrees, 26 minutes, 46 seconds North
        coords = (40.0, 26.0, 46.0)
        result = _metadata_mod._convert_gps_to_decimal(coords, "N")

        assert result is not None
        assert result > 0
        expected = 40.0 + (26.0 / 60.0) + (46.0 / 3600.0)
        assert abs(result - expected) < 0.0001

        # East reference should also be positive
        result_east = _metadata_mod._convert_gps_to_decimal(coords, "E")
        assert result_east is not None
        assert result_east > 0

    def test_convert_gps_to_decimal_south_west(self):
        """_convert_gps_to_decimal() returns negative value for S/W ref."""
        coords = (33.0, 51.0, 54.0)

        result_south = _metadata_mod._convert_gps_to_decimal(coords, "S")
        assert result_south is not None
        assert result_south < 0

        result_west = _metadata_mod._convert_gps_to_decimal(coords, "W")
        assert result_west is not None
        assert result_west < 0

        # Absolute value should match the positive computation
        expected = 33.0 + (51.0 / 60.0) + (54.0 / 3600.0)
        assert abs(abs(result_south) - expected) < 0.0001

    def test_decode_exif_value_ifd_rational_like(self):
        """_decode_exif_value() converts objects with numerator/denominator."""
        # Simulate an IFDRational-like object
        rational = MagicMock()
        rational.numerator = 22
        rational.denominator = 7

        result = _metadata_mod._decode_exif_value(rational)
        assert isinstance(result, float)
        assert abs(result - (22.0 / 7.0)) < 0.0001

    def test_decode_exif_value_ifd_rational_zero_denominator(self):
        """_decode_exif_value() handles zero denominator gracefully."""
        rational = MagicMock()
        rational.numerator = 10
        rational.denominator = 0

        result = _metadata_mod._decode_exif_value(rational)
        assert result == 0

    def test_decode_exif_value_bytes(self):
        """_decode_exif_value() decodes bytes to string."""
        result = _metadata_mod._decode_exif_value(b"Canon")
        assert result == "Canon"

    def test_decode_exif_value_tuple(self):
        """_decode_exif_value() recursively decodes tuples to lists."""
        result = _metadata_mod._decode_exif_value((1, 2, 3))
        assert result == [1, 2, 3]


# ---------------------------------------------------------------------------
# TestScreenshot
# ---------------------------------------------------------------------------

class TestScreenshot:
    """Tests for screenshot detection."""

    def test_detect_screenshot_filename_screenshot(self, tmp_path):
        """detect_screenshot() identifies files with 'Screenshot' in name."""
        file_path = tmp_path / "Screenshot_2024-01-15.png"
        file_path.write_bytes(b"fake image data")

        result = _screenshot_mod.detect_screenshot(file_path)

        assert result.is_screenshot is True
        assert result.confidence >= 0.4
        assert any("Filename" in r for r in result.reasons)

    def test_detect_screenshot_filename_screen_capture(self, tmp_path):
        """detect_screenshot() identifies files with 'screen_capture' in name."""
        file_path = tmp_path / "screen_capture_20240115.png"
        file_path.write_bytes(b"fake image data")

        result = _screenshot_mod.detect_screenshot(file_path)

        assert result.is_screenshot is True
        assert result.confidence >= 0.4

    def test_detect_screenshot_normal_photo(self, tmp_path):
        """detect_screenshot() returns not-screenshot for normal photo names."""
        file_path = tmp_path / "vacation_beach.jpg"
        file_path.write_bytes(b"fake image data")

        result = _screenshot_mod.detect_screenshot(file_path)

        assert result.is_screenshot is False
        assert result.confidence < 0.4

    def test_is_screenshot_returns_bool(self, tmp_path):
        """is_screenshot() returns a boolean value."""
        file_path = tmp_path / "Screenshot_test.png"
        file_path.write_bytes(b"fake data")

        result = _screenshot_mod.is_screenshot(file_path)
        assert isinstance(result, bool)
        assert result is True

        normal_path = tmp_path / "normal_photo.jpg"
        normal_path.write_bytes(b"fake data")

        result2 = _screenshot_mod.is_screenshot(normal_path)
        assert isinstance(result2, bool)

    def test_common_resolutions_contains_expected_sizes(self):
        """COMMON_RESOLUTIONS contains standard screen sizes."""
        resolutions = _screenshot_mod.COMMON_RESOLUTIONS

        assert (1920, 1080) in resolutions  # Full HD
        assert (3840, 2160) in resolutions  # 4K
        assert (2560, 1440) in resolutions  # QHD
        assert (1080, 1920) in resolutions  # Phone portrait FHD


# ---------------------------------------------------------------------------
# TestDuplicates
# ---------------------------------------------------------------------------

class TestDuplicates:
    """Tests for duplicates.format_file_size and get_duplicate_stats."""

    def test_format_file_size_bytes(self):
        """format_file_size() formats small sizes in bytes."""
        assert _duplicates_mod.format_file_size(0) == "0 B"
        assert _duplicates_mod.format_file_size(512) == "512 B"
        assert _duplicates_mod.format_file_size(1023) == "1023 B"

    def test_format_file_size_kb(self):
        """format_file_size() formats kilobyte sizes."""
        result = _duplicates_mod.format_file_size(1024)
        assert result == "1.0 KB"

        result = _duplicates_mod.format_file_size(1536)
        assert result == "1.5 KB"

    def test_format_file_size_mb(self):
        """format_file_size() formats megabyte sizes."""
        result = _duplicates_mod.format_file_size(1024 * 1024)
        assert result == "1.0 MB"

        result = _duplicates_mod.format_file_size(5 * 1024 * 1024)
        assert result == "5.0 MB"

    def test_format_file_size_gb(self):
        """format_file_size() formats gigabyte sizes."""
        result = _duplicates_mod.format_file_size(1024 * 1024 * 1024)
        assert result == "1.00 GB"

        result = _duplicates_mod.format_file_size(2 * 1024 * 1024 * 1024)
        assert result == "2.00 GB"

    def test_get_duplicate_stats_empty(self):
        """get_duplicate_stats() with empty groups returns zeroes."""
        stats = _duplicates_mod.get_duplicate_stats([])

        assert stats["groups"] == 0
        assert stats["duplicate_files"] == 0
        assert stats["wasted_bytes"] == 0
        assert stats["wasted_mb"] == 0.0

    def test_get_duplicate_stats_with_groups(self):
        """get_duplicate_stats() computes correct stats from groups."""
        DuplicateGroup = _duplicates_mod.DuplicateGroup

        group1 = DuplicateGroup(
            hash="abc123",
            photos=[{"id": 1}, {"id": 2}, {"id": 3}],
            keep={"id": 1},
            duplicates=[{"id": 2}, {"id": 3}],
            wasted_bytes=2048,
        )
        group2 = DuplicateGroup(
            hash="def456",
            photos=[{"id": 4}, {"id": 5}],
            keep={"id": 4},
            duplicates=[{"id": 5}],
            wasted_bytes=4096,
        )

        stats = _duplicates_mod.get_duplicate_stats([group1, group2])

        assert stats["groups"] == 2
        assert stats["duplicate_files"] == 3  # 2 from group1, 1 from group2
        assert stats["wasted_bytes"] == 6144  # 2048 + 4096
        assert abs(stats["wasted_mb"] - (6144 / (1024 * 1024))) < 0.0001


# ---------------------------------------------------------------------------
# TestScanner
# ---------------------------------------------------------------------------

class TestScanner:
    """Tests for scanner.is_image_file and IMAGE_EXTENSIONS."""

    def test_is_image_file_jpg(self):
        """is_image_file() returns True for .jpg files."""
        assert _scanner_mod.is_image_file(Path("photo.jpg")) is True

    def test_is_image_file_png(self):
        """is_image_file() returns True for .png files."""
        assert _scanner_mod.is_image_file(Path("image.png")) is True

    def test_is_image_file_txt(self):
        """is_image_file() returns False for .txt files."""
        assert _scanner_mod.is_image_file(Path("document.txt")) is False

    def test_is_image_file_uppercase_pdf(self):
        """is_image_file() handles uppercase extensions (converts to lower)."""
        # .PDF is not an image extension
        assert _scanner_mod.is_image_file(Path("file.PDF")) is False

        # .JPG should be recognized (suffix.lower() in IMAGE_EXTENSIONS)
        assert _scanner_mod.is_image_file(Path("photo.JPG")) is True

    def test_image_extensions_set(self):
        """IMAGE_EXTENSIONS contains expected formats."""
        exts = _scanner_mod.IMAGE_EXTENSIONS

        assert ".jpg" in exts
        assert ".jpeg" in exts
        assert ".png" in exts
        assert ".gif" in exts
        assert ".webp" in exts
        assert ".heic" in exts


# ---------------------------------------------------------------------------
# TestAnalyzer
# ---------------------------------------------------------------------------

class TestAnalyzer:
    """Tests for analyzer.extract_keywords."""

    def test_extract_keywords_extracts_words(self):
        """extract_keywords() extracts meaningful words from text."""
        text = "A beautiful sunset over the mountain landscape with golden light"
        keywords = _analyzer_mod.extract_keywords(text)

        assert isinstance(keywords, list)
        assert len(keywords) > 0
        assert "beautiful" in keywords
        assert "sunset" in keywords
        assert "mountain" in keywords
        assert "landscape" in keywords
        assert "golden" in keywords
        assert "light" in keywords

    def test_extract_keywords_filters_stop_words(self):
        """extract_keywords() removes common stop words."""
        text = "The image shows a large building with some trees"
        keywords = _analyzer_mod.extract_keywords(text)

        # Stop words should not appear
        assert "the" not in keywords
        assert "image" not in keywords
        assert "shows" not in keywords
        assert "with" not in keywords
        assert "some" not in keywords

        # Content words should appear
        assert "large" in keywords
        assert "building" in keywords
        assert "trees" in keywords

    def test_extract_keywords_max_ten(self):
        """extract_keywords() returns at most 10 keywords."""
        # Long text with many unique words
        text = (
            "beautiful sunset mountain landscape golden light"
            " reflecting river ancient castle towering"
            " medieval fortress bridge cobblestone village"
            " cathedral spire bell tower clock"
        )
        keywords = _analyzer_mod.extract_keywords(text)

        assert len(keywords) <= 10

    def test_extract_keywords_empty_text(self):
        """extract_keywords() returns empty list for empty text."""
        assert _analyzer_mod.extract_keywords("") == []

    def test_extract_keywords_short_words_filtered(self):
        """extract_keywords() filters words shorter than 3 characters."""
        text = "an ox is by me"
        keywords = _analyzer_mod.extract_keywords(text)

        # All words are 2 chars or fewer, or are stop words
        assert len(keywords) == 0
