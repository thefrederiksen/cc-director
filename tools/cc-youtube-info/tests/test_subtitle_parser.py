"""Tests for subtitle parsing functionality."""

import pytest
import sys
from pathlib import Path

# Add src to path for imports
sys.path.insert(0, str(Path(__file__).parent.parent))

from src.subtitle_parser import parse_subtitles, format_as_paragraphs


class TestParseSubtitles:
    """Tests for parse_subtitles function."""

    def test_parse_vtt_basic(self):
        """Test parsing basic VTT content."""
        vtt_content = """WEBVTT
Kind: captions
Language: en

00:00:00.000 --> 00:00:02.000
Hello world

00:00:02.000 --> 00:00:04.000
This is a test
"""
        result = parse_subtitles(vtt_content)
        assert "Hello world" in result
        assert "This is a test" in result
        assert "WEBVTT" not in result
        assert "-->" not in result

    def test_parse_srt_basic(self):
        """Test parsing basic SRT content."""
        srt_content = """1
00:00:00,000 --> 00:00:02,000
Hello world

2
00:00:02,000 --> 00:00:04,000
This is a test
"""
        result = parse_subtitles(srt_content)
        assert "Hello world" in result
        assert "This is a test" in result
        # Numeric cue IDs should be removed
        lines = result.split("\n")
        assert "1" not in lines
        assert "2" not in lines

    def test_remove_vtt_tags(self):
        """Test removal of VTT formatting tags."""
        vtt_content = """WEBVTT

00:00:00.000 --> 00:00:02.000
<c>Hello</c> <00:00:01.000>world

00:00:02.000 --> 00:00:04.000
<b>Bold</b> text
"""
        result = parse_subtitles(vtt_content)
        assert "<c>" not in result
        assert "</c>" not in result
        assert "<b>" not in result
        assert "</b>" not in result
        assert "<00:00:01.000>" not in result
        assert "Hello world" in result or ("Hello" in result and "world" in result)

    def test_deduplicate_lines(self):
        """Test that duplicate lines are removed."""
        vtt_content = """WEBVTT

00:00:00.000 --> 00:00:02.000
Hello world

00:00:02.000 --> 00:00:04.000
Hello world

00:00:04.000 --> 00:00:06.000
Different line

00:00:06.000 --> 00:00:08.000
Hello world
"""
        result = parse_subtitles(vtt_content)
        lines = [l.strip() for l in result.split("\n") if l.strip()]
        # "Hello world" should only appear once
        assert lines.count("Hello world") == 1
        assert "Different line" in lines

    def test_empty_content(self):
        """Test handling of empty content."""
        result = parse_subtitles("")
        assert result == ""

    def test_skip_metadata_lines(self):
        """Test that Kind: and Language: lines are skipped."""
        vtt_content = """WEBVTT
Kind: captions
Language: en

00:00:00.000 --> 00:00:02.000
Actual content
"""
        result = parse_subtitles(vtt_content)
        assert "Kind:" not in result
        assert "Language:" not in result
        assert "Actual content" in result


class TestFormatAsParagraphs:
    """Tests for format_as_paragraphs function."""

    def test_basic_formatting(self):
        """Test basic paragraph formatting."""
        transcript = "Line one.\nLine two.\nLine three.\nLine four.\nLine five.\nLine six."
        result = format_as_paragraphs(transcript, sentences_per_paragraph=3)
        paragraphs = result.split("\n\n")
        assert len(paragraphs) == 2

    def test_empty_input(self):
        """Test handling of empty input."""
        result = format_as_paragraphs("")
        assert result == ""

    def test_single_sentence(self):
        """Test single sentence input."""
        result = format_as_paragraphs("Just one sentence.")
        assert result == "Just one sentence."

    def test_question_marks(self):
        """Test that question marks are treated as sentence boundaries."""
        transcript = "What is this? Is it working? Yes it is."
        result = format_as_paragraphs(transcript, sentences_per_paragraph=2)
        # Should have 2 paragraphs: first two questions, then the statement
        paragraphs = result.split("\n\n")
        assert len(paragraphs) == 2


class TestURLValidation:
    """Tests for URL validation."""

    def test_valid_youtube_urls(self):
        """Test that valid YouTube URLs are accepted."""
        from src.youtube import validate_url

        valid_urls = [
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            "https://youtube.com/watch?v=dQw4w9WgXcQ",
            "http://www.youtube.com/watch?v=dQw4w9WgXcQ",
            "https://youtu.be/dQw4w9WgXcQ",
            "http://youtu.be/dQw4w9WgXcQ",
        ]

        for url in valid_urls:
            assert validate_url(url), f"Should be valid: {url}"

    def test_invalid_urls(self):
        """Test that invalid URLs are rejected."""
        from src.youtube import validate_url

        invalid_urls = [
            "",
            "   ",
            "not a url",
            "https://google.com",
            "https://vimeo.com/video",
            "ftp://youtube.com/watch?v=test",
        ]

        for url in invalid_urls:
            assert not validate_url(url), f"Should be invalid: {url}"


class TestVideoIdExtraction:
    """Tests for video ID extraction."""

    def test_extract_from_watch_url(self):
        """Test extracting ID from standard watch URL."""
        from src.youtube import extract_video_id

        assert extract_video_id("https://www.youtube.com/watch?v=dQw4w9WgXcQ") == "dQw4w9WgXcQ"
        assert extract_video_id("https://youtube.com/watch?v=abc123_-XYZ") == "abc123_-XYZ"

    def test_extract_from_short_url(self):
        """Test extracting ID from youtu.be short URL."""
        from src.youtube import extract_video_id

        assert extract_video_id("https://youtu.be/dQw4w9WgXcQ") == "dQw4w9WgXcQ"
        assert extract_video_id("http://youtu.be/abc123_-XYZ") == "abc123_-XYZ"

    def test_invalid_url_returns_none(self):
        """Test that invalid URLs return None."""
        from src.youtube import extract_video_id

        assert extract_video_id("https://google.com") is None
        assert extract_video_id("not a url") is None
