"""Tests for markdown parser."""

import pytest
from src.parser import parse_markdown


class TestParseMarkdown:
    """Tests for parse_markdown function."""

    def test_basic_paragraph(self):
        """Test parsing a simple paragraph."""
        result = parse_markdown("Hello, world!")
        assert "<p>Hello, world!</p>" in result.html

    def test_heading_extraction(self):
        """Test title extraction from H1."""
        result = parse_markdown("# My Document\n\nSome content.")
        assert result.title == "My Document"
        assert "<h1>" in result.html

    def test_no_title(self):
        """Test when no H1 is present."""
        result = parse_markdown("Just some text.")
        assert result.title is None

    def test_gfm_table(self):
        """Test GitHub Flavored Markdown tables."""
        md = """
| Header 1 | Header 2 |
|----------|----------|
| Cell 1   | Cell 2   |
"""
        result = parse_markdown(md)
        assert "<table>" in result.html
        assert "<th>" in result.html
        assert "<td>" in result.html

    def test_code_block(self):
        """Test fenced code blocks."""
        md = """
```python
def hello():
    print("Hello")
```
"""
        result = parse_markdown(md)
        assert "<pre>" in result.html
        assert "<code" in result.html  # May have class attribute

    def test_task_list(self):
        """Test task lists."""
        md = """
- [x] Done
- [ ] Todo
"""
        result = parse_markdown(md)
        assert "checkbox" in result.html.lower() or "task" in result.html.lower()

    def test_strikethrough(self):
        """Test strikethrough syntax."""
        result = parse_markdown("~~deleted~~")
        assert "<del>" in result.html or "<s>" in result.html

    def test_raw_content_preserved(self):
        """Test that raw content is preserved."""
        md = "# Title\n\nContent here."
        result = parse_markdown(md)
        assert result.raw == md
