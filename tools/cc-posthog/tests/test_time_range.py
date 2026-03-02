"""Tests for cc-posthog time_range module."""

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent / "src"))

from src.time_range import parse_range, validate_range


class TestParseRange:

    def test_7d(self):
        result = parse_range("7d")
        assert result["interval"] == "7 day"
        assert result["date_from"] == "-7d"
        assert "start_iso" in result

    def test_30d(self):
        result = parse_range("30d")
        assert result["interval"] == "30 day"
        assert result["date_from"] == "-30d"

    def test_1y(self):
        result = parse_range("1y")
        assert result["interval"] == "1 year"
        assert result["date_from"] == "-1y"

    def test_1w(self):
        result = parse_range("1w")
        assert result["interval"] == "1 week"
        assert result["date_from"] == "-1w"

    def test_3m(self):
        result = parse_range("3m")
        assert result["interval"] == "3 month"
        assert result["date_from"] == "-3m"

    def test_case_insensitive(self):
        result = parse_range("7D")
        assert result["interval"] == "7 day"

    def test_invalid_format_raises(self):
        with pytest.raises(ValueError, match="Invalid range"):
            parse_range("abc")

    def test_no_unit_raises(self):
        with pytest.raises(ValueError, match="Invalid range"):
            parse_range("7")

    def test_invalid_unit_raises(self):
        with pytest.raises(ValueError, match="Invalid range"):
            parse_range("7x")

    def test_empty_raises(self):
        with pytest.raises(ValueError, match="Invalid range"):
            parse_range("")


class TestValidateRange:

    def test_valid_returns_value(self):
        assert validate_range("7d") == "7d"

    def test_invalid_raises(self):
        with pytest.raises(ValueError):
            validate_range("bad")
