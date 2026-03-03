"""Tests for cc-word Word converter."""

import sys
from pathlib import Path

# Add src and cc_shared to path for testing
sys.path.insert(0, str(Path(__file__).parent.parent / "src"))
sys.path.insert(0, str(Path(__file__).parent.parent.parent / "cc_shared"))

from word_converter import _hex_to_rgb, convert_to_word
from docx.shared import RGBColor


class TestHexToRgb:
    def test_valid_6_digit_hex(self):
        result = _hex_to_rgb("#1A365D")
        assert result == RGBColor(0x1A, 0x36, 0x5D)

    def test_valid_3_digit_hex(self):
        result = _hex_to_rgb("#FFF")
        assert result == RGBColor(0xFF, 0xFF, 0xFF)

    def test_lowercase_hex(self):
        result = _hex_to_rgb("#abc123")
        assert result == RGBColor(0xAB, 0xC1, 0x23)

    def test_no_hash_returns_none(self):
        result = _hex_to_rgb("1A365D")
        assert result is None

    def test_rgba_returns_none(self):
        result = _hex_to_rgb("rgba(168, 85, 247, 0.1)")
        assert result is None

    def test_empty_returns_none(self):
        result = _hex_to_rgb("")
        assert result is None


class TestConvertToWord:
    def test_basic_conversion(self, tmp_path):
        html = "<article><h1>Title</h1><p>Body text</p></article>"
        output = tmp_path / "test.docx"
        convert_to_word(html, output, theme_name="paper")
        assert output.exists()
        assert output.stat().st_size > 0

    def test_with_table(self, tmp_path):
        html = """<article>
        <table><tr><th>Name</th><th>Value</th></tr>
        <tr><td>A</td><td>1</td></tr></table>
        </article>"""
        output = tmp_path / "table.docx"
        convert_to_word(html, output, theme_name="boardroom")
        assert output.exists()

    def test_with_code_block(self, tmp_path):
        html = "<article><pre><code>print('hello')</code></pre></article>"
        output = tmp_path / "code.docx"
        convert_to_word(html, output, theme_name="terminal")
        assert output.exists()

    def test_with_list(self, tmp_path):
        html = "<article><ul><li>Item 1</li><li>Item 2</li></ul></article>"
        output = tmp_path / "list.docx"
        convert_to_word(html, output, theme_name="paper")
        assert output.exists()

    def test_invalid_theme_still_works(self, tmp_path):
        html = "<article><p>Text</p></article>"
        output = tmp_path / "fallback.docx"
        convert_to_word(html, output, theme_name="nonexistent")
        assert output.exists()

    def test_creates_output_directory(self, tmp_path):
        html = "<article><p>Text</p></article>"
        output = tmp_path / "subdir" / "test.docx"
        convert_to_word(html, output, theme_name="paper")
        assert output.exists()
