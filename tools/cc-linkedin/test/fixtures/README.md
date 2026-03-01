# Test Fixtures: cc-linkedin

## Overview

cc-linkedin requires a live browser session with LinkedIn login. Fixtures provide mock response structures for unit testing data models and output formatting.

## Fixtures

### mock-responses/profile_response.json
- **Purpose**: Test profile data parsing and display formatting
- **Tests**: LinkedInUser model deserialization, profile display output

### mock-responses/messages_response.json
- **Purpose**: Test message thread parsing
- **Tests**: MessageThread model, unread filtering, display formatting

### mock-responses/search_response.json
- **Purpose**: Test search result parsing
- **Tests**: SearchResult model, different search types (people, posts, companies)

## Notes

NEVER use cc-browser directly with LinkedIn (account ban risk). All browser automation must go through cc-linkedin which has human-like delays built in. Integration tests should only run with explicit user approval and a test account.

## Last Validated
Date: 2026-02-26
Tool Version: 1.0.0
