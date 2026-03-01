# Test Fixtures: cc-vault

## Overview
Test data for validating credential storage key/value operations including
set, get, list, and delete commands.

## Fixtures
- `test_credentials.json` - Sample key/value pairs for bulk import testing
- `expected_list_output.txt` - Expected output format from vault list command
- `special_characters_keys.txt` - Key names with dots, dashes, underscores for edge cases
- `large_value.txt` - Long string value to test storage limits

## Notes
- NEVER store real credentials, API keys, or passwords in fixture files
- All test values must be clearly fictional (e.g., "test-api-key-12345")
- Test operations in order: set -> get -> list -> delete -> verify deletion
- Validate that get on a nonexistent key returns a clear error, not empty string
- Test key naming rules: allowed characters, max length, case sensitivity
- Vault storage location should be isolated during tests (use temp directory)
- Clean up all test keys after test run to avoid polluting the vault
- Special character tests validate keys like "my.api.key", "api-token", "DB_PASS"

## Last Validated
Date: 2026-02-26
