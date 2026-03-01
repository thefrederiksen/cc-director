# Test Fixtures: cc-hardware

## Overview

cc-hardware outputs vary by system. Fixtures provide expected output FORMAT (not values) for validation.

## Fixtures

### expected/output_format_json.json
- **Purpose**: Validate JSON output structure when --json flag is used
- **Tests**: All sections present with correct field names and types

## Notes

Hardware values are dynamic - tests should validate structure, not specific numbers. The CPU usage will always differ. RAM/disk sizes are constant per machine but vary across systems.

## Last Validated
Date: 2026-02-26
Tool Version: 1.0.0
