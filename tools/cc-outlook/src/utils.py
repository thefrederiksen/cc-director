"""Utility functions for cc-outlook."""

from datetime import datetime
from typing import Optional
import html
import re


def format_timestamp(timestamp: Optional[str]) -> str:
    """Format ISO timestamp to readable format."""
    if not timestamp:
        return "Unknown"
    try:
        # Parse ISO format
        dt = datetime.fromisoformat(timestamp.replace('Z', '+00:00'))
        return dt.strftime("%Y-%m-%d %H:%M:%S")
    except (ValueError, TypeError):
        return timestamp[:19] if timestamp else "Unknown"


def sanitize_text(text: str) -> str:
    """Remove non-ASCII characters to avoid encoding issues."""
    if not text:
        return ""

    # Replace common Unicode characters with ASCII equivalents
    replacements = {
        '\u2013': '-',   # en-dash
        '\u2014': '-',   # em-dash
        '\u2018': "'",   # left single quote
        '\u2019': "'",   # right single quote
        '\u201c': '"',   # left double quote
        '\u201d': '"',   # right double quote
        '\u2026': '...', # ellipsis
        '\u00a0': ' ',   # non-breaking space
        '\u00b7': '*',   # middle dot
        '\u2022': '*',   # bullet
        '\u2023': '>',   # triangular bullet
        '\u25cf': '*',   # black circle
        '\u25a0': '*',   # black square
        '\u2192': '->',  # rightwards arrow
        '\u2190': '<-',  # leftwards arrow
        '\u2191': '^',   # upwards arrow
        '\u2193': 'v',   # downwards arrow
        '\u00ae': '(R)', # registered sign
        '\u00a9': '(C)', # copyright sign
        '\u2122': '(TM)', # trademark sign
        '\r\n': '\n',    # Windows line ending
        '\r': '\n',      # Old Mac line ending
    }

    for unicode_char, ascii_char in replacements.items():
        text = text.replace(unicode_char, ascii_char)

    # Remove any remaining non-ASCII characters
    return text.encode('ascii', 'ignore').decode('ascii')


def truncate(text: str, max_length: int = 80) -> str:
    """Truncate text to max length with ellipsis."""
    if not text:
        return ""
    text = sanitize_text(text)
    if len(text) <= max_length:
        return text
    return text[:max_length - 3] + "..."


def strip_html(html_content: str) -> str:
    """Strip HTML tags and decode entities."""
    if not html_content:
        return ""

    # Remove HTML tags
    text = re.sub(r"<[^>]+>", "", html_content)
    # Decode HTML entities
    text = html.unescape(text)
    # Normalize whitespace
    text = " ".join(text.split())
    return text


def format_size(size_bytes: int) -> str:
    """Format byte size to human readable format."""
    for unit in ["B", "KB", "MB", "GB"]:
        if size_bytes < 1024:
            return f"{size_bytes:.1f} {unit}"
        size_bytes /= 1024
    return f"{size_bytes:.1f} TB"


def parse_email_address(address: str) -> dict:
    """
    Parse email address into name and email parts.

    Args:
        address: Email address string like "John Doe <john@example.com>"

    Returns:
        Dictionary with 'name' and 'email' keys.
    """
    if not address:
        return {"name": "", "email": ""}

    # Match "Name <email>" format
    match = re.match(r'^"?([^"<]*)"?\s*<([^>]+)>$', address.strip())
    if match:
        return {"name": match.group(1).strip(), "email": match.group(2).strip()}

    # Plain email
    return {"name": "", "email": address.strip()}


def format_message_summary(msg: dict) -> dict:
    """Format message details into a display-friendly summary."""
    return {
        "id": msg.get("id", ""),
        "from": sanitize_text(msg.get("from", "Unknown")),
        "from_name": sanitize_text(msg.get("from_name", "")),
        "to": msg.get("to", []),
        "subject": sanitize_text(msg.get("subject", "(No subject)")),
        "date": msg.get("date", ""),
        "snippet": sanitize_text(msg.get("snippet", "")),
        "is_read": msg.get("is_read", True),
    }
