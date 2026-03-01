# cc-docgen

Architecture diagram generator for CenCon documentation. Converts `architecture_manifest.yaml` into C4 model diagrams.

## Installation

### Prerequisites

1. **Python 3.10+**
2. **Graphviz** - Required for diagram rendering

```bash
# Windows (chocolatey)
choco install graphviz

# Or download from https://graphviz.org/download/
```

### Install cc-docgen

```bash
# From source
cd cc-tools/src/cc-docgen
pip install -e .

# Or install dependencies directly
pip install click pyyaml diagrams
```

## Usage

### Generate Diagrams

```bash
# Generate with defaults (looks for ./docs/cencon/architecture_manifest.yaml)
cc-docgen generate

# Specify manifest and output directory
cc-docgen generate --manifest ./docs/cencon/architecture_manifest.yaml --output ./docs/cencon/

# Generate SVG format
cc-docgen generate --format svg

# Verbose output
cc-docgen generate --verbose
```

### Validate Manifest

```bash
# Check manifest schema without generating diagrams
cc-docgen validate

# Specify manifest path
cc-docgen validate --manifest ./custom/path/architecture_manifest.yaml
```

## Output Files

| File | Description | C4 Level |
|------|-------------|----------|
| `context.png` | System context diagram showing actors and external systems | Level 1 |
| `container.png` | Container diagram showing internal structure | Level 2 |

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | Manifest file not found |
| 2 | Invalid YAML syntax |
| 3 | Missing required field in manifest |
| 4 | Graphviz not installed |
| 5 | Output directory not writable |

## Manifest Schema

The tool expects `architecture_manifest.yaml` with this structure:

```yaml
schema_version: "1.0.0"

project:
  name: "Project Name"        # Required
  description: "Description"  # Required
  version: "1.0.0"

context:
  system:
    name: "System Name"       # Required
    description: "..."        # Required
    technology: "..."         # Required

  actors:
    - id: developer           # Required
      name: Developer         # Required
      type: person            # Required: person | external_system
      description: "..."
      relationship:
        target: system_id
        description: "..."

containers:
  - id: ui_layer              # Required
    name: "UI Layer"          # Required
    technology: "WPF"         # Required
    description: "..."
    components:
      - name: "Component"
        description: "..."
```

## Integration

### With /cencon-generate skill

1. Run `/cencon-generate` to analyze source code and create manifest
2. Run `cc-docgen generate` to create diagrams from manifest

### In CI/CD

```yaml
# GitHub Actions example
- name: Generate architecture diagrams
  run: |
    pip install click pyyaml diagrams
    python -m cc-docgen generate  # Module name keeps underscore

- name: Verify diagrams exist
  run: |
    test -f docs/cencon/context.png
    test -f docs/cencon/container.png
```

## Development

```bash
# Install dev dependencies
pip install -e ".[dev]"

# Run tests
pytest

# Run with verbose output
python -m cc-docgen generate --verbose  # Module name keeps underscore
```
