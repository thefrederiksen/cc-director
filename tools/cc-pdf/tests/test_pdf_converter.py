"""Tests for cc-pdf PDF converter."""

import sys
from pathlib import Path
from unittest.mock import patch, MagicMock

# Add src to path for testing
sys.path.insert(0, str(Path(__file__).parent.parent / "src"))

from pdf_converter import find_chrome, convert_to_pdf, PAGE_SIZES


class TestPageSizes:
    def test_a4_dimensions(self):
        assert "a4" in PAGE_SIZES
        assert PAGE_SIZES["a4"]["width"] == "8.27in"
        assert PAGE_SIZES["a4"]["height"] == "11.69in"

    def test_letter_dimensions(self):
        assert "letter" in PAGE_SIZES
        assert PAGE_SIZES["letter"]["width"] == "8.5in"
        assert PAGE_SIZES["letter"]["height"] == "11in"


class TestFindChrome:
    @patch("pdf_converter.os.path.exists")
    def test_finds_chrome_windows(self, mock_exists):
        def side_effect(path):
            return "Program Files\\Google\\Chrome" in path
        mock_exists.side_effect = side_effect

        result = find_chrome()
        assert result is not None
        assert "chrome" in result.lower()

    @patch("pdf_converter.os.path.exists", return_value=False)
    def test_returns_none_when_not_found(self, mock_exists):
        result = find_chrome()
        assert result is None


class TestConvertToPdf:
    def test_invalid_page_size_raises(self, tmp_path):
        with patch("pdf_converter.find_chrome", return_value="chrome.exe"):
            try:
                convert_to_pdf("<html></html>", tmp_path / "out.pdf", page_size="tabloid")
                assert False, "Should have raised ValueError"
            except ValueError as e:
                assert "tabloid" in str(e)

    def test_no_chrome_raises(self, tmp_path):
        with patch("pdf_converter.find_chrome", return_value=None):
            try:
                convert_to_pdf("<html></html>", tmp_path / "out.pdf")
                assert False, "Should have raised RuntimeError"
            except RuntimeError as e:
                assert "Chrome not found" in str(e)
