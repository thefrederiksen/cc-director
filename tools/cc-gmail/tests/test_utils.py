"""Tests for cc-gmail utility functions."""

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


# -- format_timestamp --


class TestFormatTimestamp:
    def test_valid_timestamp_milliseconds(self):
        # 1700000000000 ms = 2023-11-14 (exact time depends on timezone)
        result = format_timestamp("1700000000000")
        assert result != "Unknown"
        # Should be a date string in YYYY-MM-DD HH:MM:SS format
        assert len(result) == 19
        assert result[4] == "-"
        assert result[7] == "-"
        assert result[10] == " "
        assert result[13] == ":"
        assert result[16] == ":"

    def test_none_returns_unknown(self):
        result = format_timestamp(None)
        assert result == "Unknown"

    def test_invalid_string_returns_unknown(self):
        result = format_timestamp("not-a-timestamp")
        assert result == "Unknown"

    def test_empty_string_returns_unknown(self):
        result = format_timestamp("")
        assert result == "Unknown"


# -- sanitize_text --


class TestSanitizeText:
    def test_replaces_em_dash(self):
        result = sanitize_text("hello\u2014world")
        assert result == "hello-world"

    def test_replaces_en_dash(self):
        result = sanitize_text("2020\u20132023")
        assert result == "2020-2023"

    def test_replaces_smart_single_quotes(self):
        result = sanitize_text("\u2018hello\u2019")
        assert result == "'hello'"

    def test_replaces_smart_double_quotes(self):
        result = sanitize_text("\u201chello\u201d")
        assert result == '"hello"'

    def test_replaces_ellipsis(self):
        result = sanitize_text("wait\u2026")
        assert result == "wait..."

    def test_replaces_non_breaking_space(self):
        result = sanitize_text("hello\u00a0world")
        assert result == "hello world"

    def test_plain_ascii_unchanged(self):
        text = "Hello, World! This is plain ASCII text. 123"
        result = sanitize_text(text)
        assert result == text

    def test_removes_remaining_non_ascii(self):
        # Characters not in the replacement map get stripped
        result = sanitize_text("cafe\u0301")  # combining accent
        # The combining accent is non-ASCII and gets stripped
        assert result == "cafe"


# -- truncate --


class TestTruncate:
    def test_short_text_returns_full(self):
        result = truncate("Hello", max_length=80)
        assert result == "Hello"

    def test_exact_length_returns_full(self):
        text = "x" * 80
        result = truncate(text, max_length=80)
        assert result == text

    def test_long_text_truncates_with_ellipsis(self):
        text = "a" * 100
        result = truncate(text, max_length=80)
        assert len(result) == 80
        assert result.endswith("...")
        assert result == "a" * 77 + "..."

    def test_custom_max_length(self):
        text = "abcdefghijklmnopqrstuvwxyz"
        result = truncate(text, max_length=10)
        assert len(result) == 10
        assert result == "abcdefg..."

    def test_truncate_also_sanitizes(self):
        # truncate calls sanitize_text internally
        text = "hello\u2014world is a test phrase"
        result = truncate(text, max_length=80)
        assert "\u2014" not in result
        assert "hello-world" in result


# -- strip_html --


class TestStripHtml:
    def test_removes_tags(self):
        result = strip_html("<p>Hello <b>world</b></p>")
        assert result == "Hello world"

    def test_decodes_html_entities(self):
        result = strip_html("&amp; &lt;tag&gt; &quot;hello&quot;")
        assert result == '& <tag> "hello"'

    def test_normalizes_whitespace(self):
        result = strip_html("<p>Hello</p>   <p>World</p>")
        assert result == "Hello World"

    def test_empty_string(self):
        result = strip_html("")
        assert result == ""

    def test_plain_text_passthrough(self):
        result = strip_html("no html here")
        assert result == "no html here"


# -- format_size --


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

    def test_one_kb_boundary(self):
        # At exactly 1024, should roll over to KB
        result = format_size(1024)
        assert result == "1.0 KB"


# -- parse_email_address --


class TestParseEmailAddress:
    def test_name_and_email_format(self):
        result = parse_email_address("John Doe <john@example.com>")
        assert result["name"] == "John Doe"
        assert result["email"] == "john@example.com"

    def test_quoted_name_format(self):
        result = parse_email_address('"Jane Smith" <jane@example.com>')
        assert result["name"] == "Jane Smith"
        assert result["email"] == "jane@example.com"

    def test_plain_email(self):
        result = parse_email_address("user@example.com")
        assert result["name"] == ""
        assert result["email"] == "user@example.com"

    def test_email_with_whitespace(self):
        result = parse_email_address("  user@example.com  ")
        assert result["email"] == "user@example.com"

    def test_name_email_with_whitespace(self):
        result = parse_email_address("  John Doe  <john@example.com>  ")
        assert result["name"] == "John Doe"
        assert result["email"] == "john@example.com"


# -- format_message_summary --


class TestFormatMessageSummary:
    def test_full_message_dict(self):
        msg = {
            "id": "msg123",
            "headers": {
                "from": "sender@example.com",
                "to": "recipient@example.com",
                "subject": "Test Subject",
                "date": "Mon, 1 Jan 2024 12:00:00 +0000",
            },
            "snippet": "This is the preview text...",
            "labels": ["INBOX", "UNREAD"],
        }
        result = format_message_summary(msg)
        assert result["id"] == "msg123"
        assert result["from"] == "sender@example.com"
        assert result["to"] == "recipient@example.com"
        assert result["subject"] == "Test Subject"
        assert result["date"] == "Mon, 1 Jan 2024 12:00:00 +0000"
        assert result["snippet"] == "This is the preview text..."
        assert result["labels"] == "INBOX, UNREAD"

    def test_missing_headers_uses_defaults(self):
        msg = {"id": "msg456"}
        result = format_message_summary(msg)
        assert result["id"] == "msg456"
        assert result["from"] == "Unknown"
        assert result["to"] == "Unknown"
        assert result["subject"] == "(No subject)"
        assert result["date"] == "Unknown"
        assert result["snippet"] == ""
        assert result["labels"] == ""

    def test_empty_message(self):
        msg = {}
        result = format_message_summary(msg)
        assert result["id"] == ""
        assert result["from"] == "Unknown"
        assert result["labels"] == ""

    def test_sanitizes_unicode_in_fields(self):
        msg = {
            "id": "msg789",
            "headers": {
                "from": "John \u201cJJ\u201d Doe <john@example.com>",
                "to": "recipient@example.com",
                "subject": "Hello\u2026 World",
                "date": "Tue, 2 Jan 2024",
            },
            "snippet": "Some text with \u2014 dashes",
            "labels": [],
        }
        result = format_message_summary(msg)
        assert "\u201c" not in result["from"]
        assert "\u201d" not in result["from"]
        assert "\u2026" not in result["subject"]
        assert "\u2014" not in result["snippet"]
        assert result["subject"] == "Hello... World"
        assert "- dashes" in result["snippet"]
