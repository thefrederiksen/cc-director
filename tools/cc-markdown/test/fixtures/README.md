# Test Fixtures: cc-markdown

## Overview

Markdown input files for testing PDF, Word, and HTML conversion across all themes.

NOTE: cc-markdown also has fixtures at `tests/fixtures/` (unit test level) and `../../tests/integration/fixtures/` (integration test level). This directory follows the cc-tools-manager standard fixture layout.

## Fixtures

### input/basic.md
- **Purpose**: Test basic markdown conversion (headings, paragraphs, lists, bold, italic)
- **Expected output**: basic.pdf, basic.html, basic.docx

### input/tables.md
- **Purpose**: Test GFM table rendering with alignment
- **Expected output**: tables.pdf

### input/code.md
- **Purpose**: Test fenced code blocks with syntax highlighting
- **Expected output**: code.pdf, code.html

### input/full-report.md
- **Purpose**: Test real-world report format with boardroom theme
- **Expected output**: full-report.pdf (boardroom theme)

## Running Tests

```bash
cc-markdown test/fixtures/input/basic.md -o test/fixtures/output/basic.pdf
cc-markdown test/fixtures/input/basic.md -o test/fixtures/output/basic.html
cc-markdown test/fixtures/input/tables.md -o test/fixtures/output/tables.pdf --theme boardroom
```

## Last Validated
Date: 2026-02-26
Tool Version: 1.0.0
