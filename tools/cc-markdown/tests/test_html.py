"""Tests for HTML generator."""

import pytest
from src.parser import parse_markdown, ParsedMarkdown
from src.html_generator import generate_html


class TestGenerateHtml:
    """Tests for generate_html function."""

    def test_basic_html_structure(self):
        """Test that output has proper HTML structure."""
        parsed = ParsedMarkdown(
            html="<p>Test</p>",
            title="Test Doc",
            raw="Test"
        )
        css = "body { color: black; }"
        result = generate_html(parsed, css)

        assert "<!DOCTYPE html>" in result
        assert "<html" in result
        assert "<head>" in result
        assert "<body>" in result
        assert "</html>" in result

    def test_title_in_head(self):
        """Test that title is in head."""
        parsed = ParsedMarkdown(
            html="<p>Content</p>",
            title="My Title",
            raw="Content"
        )
        result = generate_html(parsed, "")

        assert "<title>My Title</title>" in result

    def test_css_embedded(self):
        """Test that CSS is embedded in style tag."""
        parsed = ParsedMarkdown(
            html="<p>Content</p>",
            title="Test",
            raw="Content"
        )
        css = ".test { color: red; }"
        result = generate_html(parsed, css)

        assert "<style>" in result
        assert ".test { color: red; }" in result

    def test_content_in_article(self):
        """Test that content is wrapped in article."""
        parsed = ParsedMarkdown(
            html="<p>My content</p>",
            title="Test",
            raw="My content"
        )
        result = generate_html(parsed, "")

        assert "<article" in result
        assert "markdown-body" in result
        assert "<p>My content</p>" in result

    def test_default_title(self):
        """Test default title when none provided."""
        parsed = ParsedMarkdown(
            html="<p>Content</p>",
            title=None,
            raw="Content"
        )
        result = generate_html(parsed, "")

        assert "<title>Document</title>" in result
