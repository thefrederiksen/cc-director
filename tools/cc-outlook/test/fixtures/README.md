# Test Fixtures: cc-outlook

## Overview
Mock email JSON structures for testing Microsoft Graph API message parse and
display logic. Cannot test actual send/receive without OAuth credentials, so
fixtures provide representative Graph API response payloads for offline validation.

## Fixtures
- `message_simple.json` - Basic email with plain text body, single recipient
- `message_html.json` - Email with HTML body and contentType field
- `message_attachments.json` - Email with file attachments (metadata only)
- `message_list_response.json` - Messages endpoint response with @odata.nextLink pagination
- `message_headers.json` - Email with CC, BCC, replyTo, and internetMessageHeaders
- `calendar_events.json` - Calendar events list response for schedule display
- `search_results.json` - Search endpoint response with matched messages
- `profile_response.json` - /me endpoint response for profile display

## Notes
- All fixtures use Microsoft Graph API v1.0 JSON format
- Personal data (emails, names) must be replaced with fictional values
- OAuth tokens are never stored in fixtures -- authentication is tested separately
- Attachment content is not included; only metadata (name, contentType, size)
- Message IDs should be realistic but fictional base64-style strings
- Calendar events use ISO 8601 date format with timezone info
- The @odata context annotations should be included for realistic responses

## Last Validated
Date: 2026-02-26
