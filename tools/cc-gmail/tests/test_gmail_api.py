"""Tests for GmailClient._decode_base64 and _extract_body methods."""

import base64
from unittest.mock import patch, MagicMock

import pytest

from src.gmail_api import GmailClient


@pytest.fixture
def gmail_client():
    """Create a GmailClient instance without hitting the Google API."""
    with patch("src.gmail_api.build") as mock_build:
        mock_build.return_value = MagicMock()
        client = GmailClient(credentials=MagicMock())
    return client


# -- _decode_base64 --


class TestDecodeBase64:
    def test_valid_base64_string(self, gmail_client):
        # Encode "Hello, World!" in URL-safe base64
        original = "Hello, World!"
        encoded = base64.urlsafe_b64encode(original.encode("utf-8")).decode("utf-8")
        # Strip padding to simulate Gmail behavior
        encoded = encoded.rstrip("=")
        result = gmail_client._decode_base64(encoded)
        assert result == original

    def test_base64_with_missing_padding(self, gmail_client):
        # "Test" -> base64url = "VGVzdA==" -> strip padding -> "VGVzdA"
        original = "Test"
        encoded_no_padding = "VGVzdA"
        result = gmail_client._decode_base64(encoded_no_padding)
        assert result == original

    def test_base64_already_padded(self, gmail_client):
        original = "Already padded"
        encoded = base64.urlsafe_b64encode(original.encode("utf-8")).decode("utf-8")
        result = gmail_client._decode_base64(encoded)
        assert result == original

    def test_base64_with_url_safe_chars(self, gmail_client):
        # Content that produces + and / in standard base64 uses - and _ in urlsafe
        original = "data with special bytes: \xff\xfe"
        encoded = base64.urlsafe_b64encode(original.encode("utf-8")).decode("utf-8")
        encoded = encoded.rstrip("=")
        result = gmail_client._decode_base64(encoded)
        assert result == original

    def test_empty_content(self, gmail_client):
        encoded = base64.urlsafe_b64encode(b"").decode("utf-8")
        result = gmail_client._decode_base64(encoded)
        assert result == ""


# -- _extract_body --


class TestExtractBody:
    def test_simple_text_plain_payload(self, gmail_client):
        """Payload with body.data directly (no parts)."""
        text = "This is the email body."
        encoded = base64.urlsafe_b64encode(text.encode("utf-8")).decode("utf-8")
        payload = {
            "mimeType": "text/plain",
            "body": {"data": encoded, "size": len(text)},
        }
        result = gmail_client._extract_body(payload)
        assert result == text

    def test_multipart_text_plain_and_html(self, gmail_client):
        """Multipart payload with text/plain and text/html -- should prefer text/plain."""
        plain_text = "Plain text version"
        html_text = "<p>HTML version</p>"
        plain_encoded = base64.urlsafe_b64encode(
            plain_text.encode("utf-8")
        ).decode("utf-8")
        html_encoded = base64.urlsafe_b64encode(
            html_text.encode("utf-8")
        ).decode("utf-8")

        payload = {
            "mimeType": "multipart/alternative",
            "body": {"size": 0},
            "parts": [
                {
                    "mimeType": "text/plain",
                    "body": {"data": plain_encoded, "size": len(plain_text)},
                },
                {
                    "mimeType": "text/html",
                    "body": {"data": html_encoded, "size": len(html_text)},
                },
            ],
        }
        result = gmail_client._extract_body(payload)
        assert result == plain_text

    def test_nested_multipart(self, gmail_client):
        """Nested multipart/mixed containing multipart/alternative with text/plain."""
        plain_text = "Nested plain text"
        plain_encoded = base64.urlsafe_b64encode(
            plain_text.encode("utf-8")
        ).decode("utf-8")

        payload = {
            "mimeType": "multipart/mixed",
            "body": {"size": 0},
            "parts": [
                {
                    "mimeType": "multipart/alternative",
                    "body": {"size": 0},
                    "parts": [
                        {
                            "mimeType": "text/plain",
                            "body": {
                                "data": plain_encoded,
                                "size": len(plain_text),
                            },
                        },
                    ],
                },
                {
                    "mimeType": "application/pdf",
                    "filename": "attachment.pdf",
                    "body": {"attachmentId": "att123", "size": 5000},
                },
            ],
        }
        result = gmail_client._extract_body(payload)
        assert result == plain_text

    def test_html_only_fallback(self, gmail_client):
        """When no text/plain part exists, should fall back to text/html."""
        html_text = "<p>Only HTML available</p>"
        html_encoded = base64.urlsafe_b64encode(
            html_text.encode("utf-8")
        ).decode("utf-8")

        payload = {
            "mimeType": "multipart/alternative",
            "body": {"size": 0},
            "parts": [
                {
                    "mimeType": "text/html",
                    "body": {"data": html_encoded, "size": len(html_text)},
                },
            ],
        }
        result = gmail_client._extract_body(payload)
        assert result == html_text

    def test_empty_payload(self, gmail_client):
        """Payload with no body data and no parts returns empty string."""
        payload = {
            "mimeType": "text/plain",
            "body": {"size": 0},
        }
        result = gmail_client._extract_body(payload)
        assert result == ""

    def test_multipart_with_empty_text_plain(self, gmail_client):
        """text/plain part exists but has no data -- should fall back to HTML."""
        html_text = "<p>HTML fallback</p>"
        html_encoded = base64.urlsafe_b64encode(
            html_text.encode("utf-8")
        ).decode("utf-8")

        payload = {
            "mimeType": "multipart/alternative",
            "body": {"size": 0},
            "parts": [
                {
                    "mimeType": "text/plain",
                    "body": {"size": 0},
                },
                {
                    "mimeType": "text/html",
                    "body": {"data": html_encoded, "size": len(html_text)},
                },
            ],
        }
        result = gmail_client._extract_body(payload)
        assert result == html_text
