# Test Fixtures: cc_shared

## Overview

Config file fixtures for testing cc_shared configuration loading, saving, and error recovery. These are real JSON files that exercise every code path in config.py.

## Fixtures

### input/config_full.json
- **Purpose**: Test loading a complete config with all sections populated
- **Tests**: Full config parsing, photo sources with priorities, all LLM provider settings
- **Expected output**: expected/config_full_roundtrip.json (identical after save/reload)

### input/config_minimal.json
- **Purpose**: Test loading a config with only the LLM section
- **Tests**: Partial config merges correctly with defaults for missing sections
- **Expected output**: All unspecified sections should have default values

### input/config_empty.json
- **Purpose**: Test loading an empty JSON object
- **Tests**: All sections fall back to defaults when nothing is specified
- **Expected output**: expected/config_defaults.json (note: vault_path is dynamic)

### input/config_corrupted.json
- **Purpose**: Test error recovery with invalid JSON
- **Tests**: Corrupted file does not crash, config uses defaults instead
- **Expected output**: Same as config_defaults.json - graceful fallback

## Expected Output Notes

- **config_full_roundtrip.json**: Exact match after load -> to_dict() cycle
- **config_defaults.json**: Reference for default values. The vault_path field is marked DYNAMIC because it depends on the system (checks LOCALAPPDATA, D:/Vault existence)

## Running Tests

```bash
cd src/cc_shared
pip install -e ".[dev]"
python -m pytest tests/ -v
```

## Last Validated
Date: 2026-02-26
Tool Version: 0.1.0
