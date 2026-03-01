# Test Fixtures: cc-click

## Overview

cc-click requires a live Windows desktop with UI applications. Fixtures provide expected output formats for validation.

## Fixtures

### expected/list_windows_format.json
- **Purpose**: Validate JSON output structure of list-windows command
- **Tests**: Output contains title, processName, processId, handle fields

### expected/click_response_format.json
- **Purpose**: Validate JSON output structure of click command
- **Tests**: Output contains clicked, automationId, name fields

### expected/error_format.json
- **Purpose**: Validate JSON error output structure
- **Tests**: Error responses contain "error" field with descriptive message

## Notes

cc-click tests are integration tests that require a Windows desktop. Unit tests can validate JSON output structure and argument parsing. Full integration tests should launch a known app (e.g., Notepad) for predictable element discovery.

## Last Validated
Date: 2026-02-26
Tool Version: 1.0.0
