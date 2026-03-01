# Test Fixtures: cc-youtube-info

## Overview
Fixtures describe expected output format and structure for YouTube metadata
retrieval. Since the tool fetches live data from YouTube URLs, fixtures focus
on response schema validation rather than exact content matching.

## Fixtures
- `expected_schema.json` - JSON schema defining required fields in output
- `sample_video_output.json` - Example output for a known public video
- `sample_playlist_output.json` - Example output for a known public playlist
- `known_video_urls.txt` - List of stable public YouTube URLs for live testing
- `invalid_urls.txt` - Malformed or nonexistent URLs for error handling tests

## Notes
- This tool fetches live data from YouTube, so fixtures cannot guarantee exact output
- Test strategy: validate output structure (required fields, types) not exact values
- Required output fields to validate: title, channel, duration, upload date, view count
- Use well-known, long-lived public videos (e.g., official music videos) for stable tests
- Invalid URL tests should produce clear error messages, not crashes
- Rate limiting may affect test runs; add delays between requests if needed
- Do not store API keys or authentication tokens in fixtures
- Video metadata (view count, likes) changes over time -- only validate field presence

## Last Validated
Date: 2026-02-26
