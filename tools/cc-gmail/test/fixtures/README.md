# Test Fixtures: cc-gmail

## Overview
Mock email JSON structures for testing Gmail message parse and display logic.
Cannot test actual send/receive without OAuth credentials, so fixtures
provide representative Gmail API response payloads for offline validation.

## Fixtures
- `message_simple.json` - Basic email with plain text body, single recipient
- `message_html.json` - Email with HTML body content
- `message_attachments.json` - Email with file attachments (metadata only)
- `message_thread.json` - Thread with multiple messages for conversation display
- `message_list_response.json` - Gmail list endpoint response with pagination token
- `message_headers.json` - Email with complex headers (CC, BCC, Reply-To)
- `label_list.json` - Gmail labels response for folder/label display
- `search_results.json` - Search endpoint response with matched messages

## Notes
- All fixtures use the Gmail API v1 JSON format
- Personal data (emails, names) must be replaced with fictional values
- OAuth tokens are never stored in fixtures -- authentication is tested separately
- Attachment content is not included; only metadata (filename, mimeType, size)
- Message IDs and thread IDs should be realistic but fictional hex strings

## Last Validated
Date: 2026-02-26
