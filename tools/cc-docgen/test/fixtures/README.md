# Test Fixtures: cc-docgen

## Overview

YAML manifest files for testing C4 diagram generation and schema validation.

## Fixtures

### input/valid_manifest.yaml
- **Purpose**: Test full diagram generation with actors, containers, and relationships
- **Tests**: Context diagram generation, container diagram generation, actor/system rendering
- **Expected output**: context.png and container.png in output/

### input/minimal_manifest.yaml
- **Purpose**: Test bare minimum valid manifest
- **Tests**: Validates minimum required fields are sufficient

### input/invalid_yaml.yaml
- **Purpose**: Test error handling for malformed YAML
- **Tests**: Parser error recovery, clear error messages

## Notes

Diagram generation requires Graphviz installed. Tests that generate actual PNG/SVG files should be marked as integration tests.

## Last Validated
Date: 2026-02-26
Tool Version: 0.1.0
