"""Subtitle parsing utilities for VTT and SRT formats."""

import re


# Regex patterns for subtitle parsing
TIMESTAMP_PATTERN = re.compile(
    r"^\d{2}:\d{2}:\d{2}[.,]\d{3}\s*-->\s*\d{2}:\d{2}:\d{2}[.,]\d{3}"
)
VTT_TAG_PATTERN = re.compile(r"<[^>]+>")


def parse_subtitles(content: str) -> str:
    """
    Parse VTT or SRT subtitle content into clean text.

    Removes:
    - WEBVTT headers and metadata
    - Timestamp lines
    - Numeric cue identifiers
    - VTT tags (<c>, </c>, timing tags, etc.)
    - Duplicate consecutive lines (common in auto-generated captions)

    Args:
        content: Raw VTT or SRT subtitle content

    Returns:
        Cleaned transcript text with one line per unique caption
    """
    lines = content.split("\n")
    transcript_lines: list[str] = []
    seen_lines: set[str] = set()

    for line in lines:
        trimmed = line.strip()

        # Skip empty lines
        if not trimmed:
            continue

        # Skip WEBVTT headers
        if trimmed.startswith("WEBVTT"):
            continue
        if trimmed.startswith("Kind:"):
            continue
        if trimmed.startswith("Language:"):
            continue

        # Skip timestamp lines
        if TIMESTAMP_PATTERN.match(trimmed):
            continue

        # Skip numeric-only lines (cue identifiers in SRT)
        if trimmed.isdigit():
            continue

        # Remove VTT tags like <c>, </c>, <00:00:00.000>, etc.
        clean_line = VTT_TAG_PATTERN.sub("", trimmed).strip()

        # Skip if empty after cleaning
        if not clean_line:
            continue

        # Skip duplicates
        if clean_line in seen_lines:
            continue

        seen_lines.add(clean_line)
        transcript_lines.append(clean_line)

    return "\n".join(transcript_lines)


def format_as_paragraphs(transcript: str, sentences_per_paragraph: int = 3) -> str:
    """
    Format transcript text into paragraphs for readability.

    Args:
        transcript: Clean transcript text (one line per caption)
        sentences_per_paragraph: Approximate sentences per paragraph

    Returns:
        Paragraph-formatted transcript
    """
    lines = transcript.split("\n")
    if not lines:
        return ""

    # Join all lines, then split by sentence endings
    text = " ".join(lines)

    # Split on sentence boundaries (., ?, !)
    sentences = re.split(r"(?<=[.!?])\s+", text)

    paragraphs: list[str] = []
    current_paragraph: list[str] = []

    for sentence in sentences:
        sentence = sentence.strip()
        if not sentence:
            continue

        current_paragraph.append(sentence)

        if len(current_paragraph) >= sentences_per_paragraph:
            paragraphs.append(" ".join(current_paragraph))
            current_paragraph = []

    # Add remaining sentences
    if current_paragraph:
        paragraphs.append(" ".join(current_paragraph))

    return "\n\n".join(paragraphs)
