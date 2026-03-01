"""Tests for cc-outlook utility functions."""

import pytest

from src.utils import (
    format_timestamp,
    sanitize_text,
    truncate,
    strip_html,
    format_size,
    parse_email_address,
    format_message_summary,
)


# ---- format_timestamp ----

class TestFormatTimestamp:

    def test_valid_iso_timestamp(self):
        result = format_timestamp("2026-02-15T14:30:00Z")
        assert result == "2026-02-15 14:30:00"

    def test_valid_iso_with_offset(self):
        result = format_timestamp("2026-02-15T14:30:00+00:00")
        assert result == "2026-02-15 14:30:00"

    def test_none_returns_unknown(self):
        assert format_timestamp(None) == "Unknown"

    def test_empty_string_returns_unknown(self):
        assert format_timestamp("") == "Unknown"


# ---- sanitize_text ----

class TestSanitizeText:

    def test_replaces_unicode_with_ascii(self):
        # en-dash, em-dash, smart quotes, ellipsis
        text = "\u2013 \u2014 \u2018hello\u2019 \u201cworld\u201d \u2026"
        result = sanitize_text(text)
        assert result == "- - 'hello' \"world\" ..."

    def test_plain_ascii_unchanged(self):
        text = "Hello, world! This is plain ASCII text."
        result = sanitize_text(text)
        assert result == text

    def test_empty_string_returns_empty(self):
        assert sanitize_text("") == ""

    def test_none_returns_empty(self):
        assert sanitize_text(None) == ""

    def test_arrows_replaced(self):
        text = "\u2192 \u2190 \u2191 \u2193"
        result = sanitize_text(text)
        assert result == "-> <- ^ v"

    def test_trademark_symbols_replaced(self):
        text = "\u00ae \u00a9 \u2122"
        result = sanitize_text(text)
        assert result == "(R) (C) (TM)"

    def test_remaining_non_ascii_stripped(self):
        # Characters not in the replacement map get removed
        text = "hello\u00e9world"  # e with acute accent
        result = sanitize_text(text)
        assert result == "helloworld"


# ---- truncate ----

class TestTruncate:

    def test_short_text_unchanged(self):
        text = "Short text"
        result = truncate(text, max_length=80)
        assert result == text

    def test_long_text_truncated_with_ellipsis(self):
        text = "A" * 100
        result = truncate(text, max_length=20)
        assert len(result) == 20
        assert result.endswith("...")
        assert result == "A" * 17 + "..."

    def test_exact_length_not_truncated(self):
        text = "A" * 80
        result = truncate(text, max_length=80)
        assert result == text

    def test_empty_string_returns_empty(self):
        assert truncate("") == ""

    def test_none_returns_empty(self):
        assert truncate(None) == ""

    def test_default_max_length_is_80(self):
        text = "B" * 81
        result = truncate(text)
        assert len(result) == 80
        assert result.endswith("...")


# ---- strip_html ----

class TestStripHtml:

    def test_removes_tags(self):
        html_text = "<p>Hello <b>world</b></p>"
        result = strip_html(html_text)
        assert result == "Hello world"

    def test_decodes_entities(self):
        html_text = "Tom &amp; Jerry &lt;3"
        result = strip_html(html_text)
        assert result == "Tom & Jerry <3"

    def test_normalizes_whitespace(self):
        html_text = "<p>Hello</p>   <p>World</p>"
        result = strip_html(html_text)
        assert result == "Hello World"

    def test_empty_string_returns_empty(self):
        assert strip_html("") == ""

    def test_none_returns_empty(self):
        assert strip_html(None) == ""

    def test_complex_html(self):
        html_text = '<div class="content"><h1>Title</h1> <p>Body &quot;text&quot;</p></div>'
        result = strip_html(html_text)
        assert result == 'Title Body "text"'


# ---- format_size ----

class TestFormatSize:

    def test_bytes(self):
        result = format_size(500)
        assert result == "500.0 B"

    def test_kilobytes(self):
        result = format_size(2048)
        assert result == "2.0 KB"

    def test_megabytes(self):
        result = format_size(5 * 1024 * 1024)
        assert result == "5.0 MB"

    def test_gigabytes(self):
        result = format_size(3 * 1024 * 1024 * 1024)
        assert result == "3.0 GB"

    def test_terabytes(self):
        result = format_size(2 * 1024 * 1024 * 1024 * 1024)
        assert result == "2.0 TB"

    def test_zero_bytes(self):
        result = format_size(0)
        assert result == "0.0 B"

    def test_one_byte(self):
        result = format_size(1)
        assert result == "1.0 B"

    def test_just_under_1kb(self):
        result = format_size(1023)
        assert result == "1023.0 B"

    def test_exactly_1kb(self):
        result = format_size(1024)
        assert result == "1.0 KB"


# ---- parse_email_address ----

class TestParseEmailAddress:

    def test_name_and_email_format(self):
        result = parse_email_address("John Doe <john@example.com>")
        assert result == {"name": "John Doe", "email": "john@example.com"}

    def test_plain_email(self):
        result = parse_email_address("john@example.com")
        assert result == {"name": "", "email": "john@example.com"}

    def test_quoted_name_format(self):
        result = parse_email_address('"Jane Smith" <jane@example.com>')
        assert result == {"name": "Jane Smith", "email": "jane@example.com"}

    def test_empty_string(self):
        result = parse_email_address("")
        assert result == {"name": "", "email": ""}

    def test_none_returns_empty(self):
        result = parse_email_address(None)
        assert result == {"name": "", "email": ""}

    def test_email_with_whitespace(self):
        result = parse_email_address("  user@domain.org  ")
        assert result == {"name": "", "email": "user@domain.org"}


# ---- format_message_summary ----

class TestFormatMessageSummary:

    def test_full_message_dict(self):
        msg = {
            "id": "msg-123",
            "from": "sender@example.com",
            "from_name": "Sender Name",
            "to": ["recipient@example.com"],
            "subject": "Test Subject",
            "date": "2026-02-15",
            "snippet": "This is a preview...",
            "is_read": False,
        }
        result = format_message_summary(msg)
        assert result["id"] == "msg-123"
        assert result["from"] == "sender@example.com"
        assert result["from_name"] == "Sender Name"
        assert result["to"] == ["recipient@example.com"]
        assert result["subject"] == "Test Subject"
        assert result["date"] == "2026-02-15"
        assert result["snippet"] == "This is a preview..."
        assert result["is_read"] is False

    def test_empty_message_dict_uses_defaults(self):
        result = format_message_summary({})
        assert result["id"] == ""
        assert result["from"] == "Unknown"
        assert result["from_name"] == ""
        assert result["to"] == []
        assert result["subject"] == "(No subject)"
        assert result["date"] == ""
        assert result["snippet"] == ""
        assert result["is_read"] is True

    def test_sanitizes_unicode_in_fields(self):
        msg = {
            "subject": "Meeting \u2013 Q1 Review",
            "from": "John\u2019s Account",
            "from_name": "John\u2019s Name",
            "snippet": "Let\u2019s discuss\u2026",
        }
        result = format_message_summary(msg)
        assert result["subject"] == "Meeting - Q1 Review"
        assert result["from"] == "John's Account"
        assert result["from_name"] == "John's Name"
        assert result["snippet"] == "Let's discuss..."
