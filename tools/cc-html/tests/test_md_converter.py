"""Tests for cc-html markdown converter (HTML -> Markdown)."""

import base64
from pathlib import Path

import pytest

from src.md_converter import convert_html_to_markdown


class TestConvertHtmlToMarkdown:
    """Tests for convert_html_to_markdown."""

    def test_basic_heading_and_paragraph(self, tmp_path):
        html = "<h1>Title</h1><p>Hello world</p>"
        output = tmp_path / "test.md"

        md = convert_html_to_markdown(html, output)

        assert "# Title" in md
        assert "Hello world" in md

    def test_preserves_links(self, tmp_path):
        html = '<p>Visit <a href="https://example.com">example</a></p>'
        output = tmp_path / "test.md"

        md = convert_html_to_markdown(html, output)

        assert "[example](https://example.com)" in md

    def test_preserves_list(self, tmp_path):
        html = "<ul><li>One</li><li>Two</li></ul>"
        output = tmp_path / "test.md"

        md = convert_html_to_markdown(html, output)

        assert "- One" in md
        assert "- Two" in md

    def test_preserves_remote_image_urls(self, tmp_path):
        html = '<img src="https://example.com/logo.png" alt="Logo">'
        output = tmp_path / "test.md"

        md = convert_html_to_markdown(html, output)

        assert "https://example.com/logo.png" in md
        assert "Logo" in md

    def test_extracts_base64_image(self, tmp_path):
        # Create a tiny 1x1 PNG as base64
        pixel = base64.b64encode(b"\x89PNG\r\n\x1a\n" + b"\x00" * 50).decode()
        html = f'<p>Before</p><img src="data:image/png;base64,{pixel}" alt="pixel"><p>After</p>'
        output = tmp_path / "test.md"

        md = convert_html_to_markdown(html, output)

        # Should have created images directory
        images_dir = tmp_path / "test_images"
        assert images_dir.exists()
        assert any(images_dir.iterdir())

        # Markdown should reference the extracted image
        assert "test_images/" in md

    def test_extracts_local_image(self, tmp_path):
        # Create a local image file
        img_data = b"\x89PNG\r\n\x1a\n" + b"\x00" * 50
        local_img = tmp_path / "photo.png"
        local_img.write_bytes(img_data)

        html = '<img src="photo.png" alt="Photo">'
        output = tmp_path / "result.md"

        md = convert_html_to_markdown(html, output, input_dir=tmp_path)

        images_dir = tmp_path / "result_images"
        assert images_dir.exists()
        assert "result_images/" in md

    def test_no_images_no_directory_created(self, tmp_path):
        html = "<h1>No images here</h1><p>Just text</p>"
        output = tmp_path / "test.md"

        md = convert_html_to_markdown(html, output)

        images_dir = tmp_path / "test_images"
        assert not images_dir.exists()
        assert "# No images here" in md

    def test_cleans_excessive_blank_lines(self, tmp_path):
        html = "<p>A</p><br><br><br><br><p>B</p>"
        output = tmp_path / "test.md"

        md = convert_html_to_markdown(html, output)

        # Should not have more than 2 consecutive newlines
        assert "\n\n\n" not in md

    def test_output_ends_with_newline(self, tmp_path):
        html = "<p>Hello</p>"
        output = tmp_path / "test.md"

        md = convert_html_to_markdown(html, output)

        assert md.endswith("\n")

    def test_table_conversion(self, tmp_path):
        html = """
        <table>
            <thead><tr><th>Name</th><th>Age</th></tr></thead>
            <tbody><tr><td>Alice</td><td>30</td></tr></tbody>
        </table>
        """
        output = tmp_path / "test.md"

        md = convert_html_to_markdown(html, output)

        assert "Name" in md
        assert "Alice" in md
