"""Tests for cc-pdf markdown converter (PDF -> Markdown)."""

from pathlib import Path

import pymupdf
import pytest

from src.md_converter import convert_pdf_to_markdown, _median


def _create_pdf(path: Path, pages: list[list[tuple[str, float]]]) -> Path:
    """Helper: create a test PDF with given page content.

    Args:
        path: Output path.
        pages: List of pages, each page is a list of (text, font_size) tuples.
    """
    doc = pymupdf.open()
    for page_items in pages:
        page = doc.new_page()
        y = 72  # Start 1 inch from top
        for text, size in page_items:
            page.insert_text((72, y), text, fontsize=size)
            y += size * 1.5
    doc.save(str(path))
    doc.close()
    return path


class TestMedian:
    """Tests for the _median helper."""

    def test_empty_list(self):
        assert _median([]) == 0.0

    def test_single_value(self):
        assert _median([5.0]) == 5.0

    def test_odd_count(self):
        assert _median([1.0, 3.0, 5.0]) == 3.0

    def test_even_count(self):
        assert _median([1.0, 2.0, 3.0, 4.0]) == 2.5


class TestConvertPdfToMarkdown:
    """Tests for convert_pdf_to_markdown."""

    def test_basic_text(self, tmp_path):
        pdf_path = _create_pdf(
            tmp_path / "test.pdf",
            [[("Hello world", 12.0), ("Second line", 12.0)]],
        )
        output = tmp_path / "test.md"

        md = convert_pdf_to_markdown(pdf_path, output)

        assert "Hello world" in md
        assert "Second line" in md

    def test_heading_detection_by_font_size(self, tmp_path):
        pdf_path = _create_pdf(
            tmp_path / "test.pdf",
            [
                [
                    ("Big Title", 24.0),
                    ("Normal text here", 12.0),
                    ("More normal text", 12.0),
                ]
            ],
        )
        output = tmp_path / "test.md"

        md = convert_pdf_to_markdown(pdf_path, output)

        # Big title should be detected as heading
        assert "# Big Title" in md
        assert "Normal text here" in md

    def test_multiple_pages(self, tmp_path):
        pdf_path = _create_pdf(
            tmp_path / "test.pdf",
            [
                [("Page 1 content", 12.0)],
                [("Page 2 content", 12.0)],
            ],
        )
        output = tmp_path / "test.md"

        md = convert_pdf_to_markdown(pdf_path, output)

        assert "Page 1 content" in md
        assert "Page 2 content" in md

    def test_no_images_no_directory(self, tmp_path):
        pdf_path = _create_pdf(
            tmp_path / "test.pdf",
            [[("Just text", 12.0)]],
        )
        output = tmp_path / "test.md"

        md = convert_pdf_to_markdown(pdf_path, output)

        images_dir = tmp_path / "test_images"
        assert not images_dir.exists()

    def test_output_ends_with_newline(self, tmp_path):
        pdf_path = _create_pdf(
            tmp_path / "test.pdf",
            [[("Hello", 12.0)]],
        )
        output = tmp_path / "test.md"

        md = convert_pdf_to_markdown(pdf_path, output)

        assert md.endswith("\n")

    def test_no_excessive_blank_lines(self, tmp_path):
        pdf_path = _create_pdf(
            tmp_path / "test.pdf",
            [[("A", 12.0), ("B", 12.0), ("C", 12.0)]],
        )
        output = tmp_path / "test.md"

        md = convert_pdf_to_markdown(pdf_path, output)

        assert "\n\n\n" not in md

    def test_image_extraction(self, tmp_path):
        # Create a PDF with an embedded image
        doc = pymupdf.open()
        page = doc.new_page()

        # Insert text
        page.insert_text((72, 72), "Document with image", fontsize=12)

        # Create a tiny PNG and insert it
        import struct
        import zlib

        sig = b"\x89PNG\r\n\x1a\n"
        ihdr_data = struct.pack(">IIBBBBB", 1, 1, 8, 2, 0, 0, 0)
        ihdr_crc = zlib.crc32(b"IHDR" + ihdr_data) & 0xFFFFFFFF
        ihdr = struct.pack(">I", 13) + b"IHDR" + ihdr_data + struct.pack(">I", ihdr_crc)
        raw = zlib.compress(b"\x00\xFF\x00\x00")
        idat_crc = zlib.crc32(b"IDAT" + raw) & 0xFFFFFFFF
        idat = struct.pack(">I", len(raw)) + b"IDAT" + raw + struct.pack(">I", idat_crc)
        iend_crc = zlib.crc32(b"IEND") & 0xFFFFFFFF
        iend = struct.pack(">I", 0) + b"IEND" + struct.pack(">I", iend_crc)
        png_data = sig + ihdr + idat + iend

        img_path = tmp_path / "tiny.png"
        img_path.write_bytes(png_data)

        rect = pymupdf.Rect(72, 100, 172, 200)
        page.insert_image(rect, filename=str(img_path))

        pdf_path = tmp_path / "with_img.pdf"
        doc.save(str(pdf_path))
        doc.close()

        output = tmp_path / "with_img.md"

        md = convert_pdf_to_markdown(pdf_path, output)

        images_dir = tmp_path / "with_img_images"
        assert images_dir.exists()
        assert any(images_dir.iterdir())
        assert "with_img_images/" in md
