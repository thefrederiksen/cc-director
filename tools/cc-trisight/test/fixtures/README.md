# Test Fixtures: cc-trisight

## Overview

Screenshot files and expected detection output for testing the three-tier detection pipeline.

## Fixtures

### expected/detect_output_schema.json
- **Purpose**: Validate JSON output structure of detect command
- **Tests**: Output has success, elementCount, elements array with id/type/bounds/confidence

### expected/element_schema.json
- **Purpose**: Validate individual element structure
- **Tests**: Each element has id, type, name, bounds, center, source, confidence

## Notes

Full integration tests require a Windows desktop. The trisight test_output/ directory in the source may contain previous test artifacts from development.

## Last Validated
Date: 2026-02-26
Tool Version: 1.0.0
