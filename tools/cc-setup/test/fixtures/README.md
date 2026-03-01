# Test Fixtures: cc-setup

## Overview

cc-setup is an installer tool - fixtures simulate GitHub API responses for testing without network access.

## Fixtures

### input/release_response.json
- **Purpose**: Mock GitHub releases/latest API response
- **Tests**: Release parsing, asset URL extraction, version detection

### expected/tool_list.txt
- **Purpose**: Expected list of tools the installer downloads
- **Tests**: Validates installer knows all current tools

## Notes

cc-setup tests are primarily unit tests with mocked HTTP calls since the tool requires network access to GitHub.

## Last Validated
Date: 2026-02-26
Tool Version: 1.0.0
