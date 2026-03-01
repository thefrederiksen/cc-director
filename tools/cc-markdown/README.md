# cc-markdown

Convert Markdown to beautifully styled PDF, Word, and HTML documents.

Part of the [CC Tools](../../README.md) suite.

## Features

- **Multiple Output Formats:** PDF, Word (.docx), HTML
- **7 Built-in Themes:** Professional styles for any use case
- **Custom CSS:** Use your own stylesheets
- **Cross-Platform:** Windows, Linux, macOS
- **Single Executable:** No installation required

## Installation

Download from the releases page or install from source:

```bash
pip install -e .
```

## Usage

```bash
# Basic conversion (format detected from extension)
cc-markdown input.md -o output.pdf
cc-markdown input.md -o output.docx
cc-markdown input.md -o output.html

# With theme
cc-markdown input.md -o output.pdf --theme boardroom

# With custom CSS
cc-markdown input.md -o output.pdf --css custom.css

# List themes
cc-markdown --themes

# Help
cc-markdown --help
```

## Themes

| Theme | Description |
|-------|-------------|
| **paper** | Minimal, clean, elegant (default) |
| **boardroom** | Corporate, executive style with serif fonts |
| **terminal** | Technical, monospace with dark-friendly colors |
| **spark** | Creative, colorful, modern |
| **thesis** | Academic, scholarly with proper citations style |
| **obsidian** | Dark theme with subtle highlights |
| **blueprint** | Technical documentation style |

## Options

| Option | Description | Default |
|--------|-------------|---------|
| `-o, --output` | Output file path | Required |
| `--theme` | Built-in theme name | paper |
| `--css` | Custom CSS file path | None |
| `--page-size` | Page size (a4, letter) | a4 |
| `--margin` | Page margin | 1in |

## Requirements

- **PDF Output**: Requires Google Chrome installed (uses Chrome headless for rendering)
- **Word Output**: No additional requirements
- **HTML Output**: No additional requirements

## Development

```bash
# Install dependencies
pip install -e ".[dev]"

# Run tests
pytest tests/ -v

# Build executable
pyinstaller cc-markdown.spec --clean --noconfirm
```

## License

MIT License - see [LICENSE](../../LICENSE)
