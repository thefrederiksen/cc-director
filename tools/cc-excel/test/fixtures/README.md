# Test Fixtures: cc-excel

## Overview

These fixtures test CSV, JSON, and Markdown to Excel conversion with type inference and formatting.

## Fixtures

### input/sales.csv
- **Purpose**: Test CSV parsing with mixed data types
- **Tests**: Currency detection, percentage detection, date parsing, integer parsing
- **Expected output**: expected/sales_types.json documents expected column types

### input/employees.json
- **Purpose**: Test JSON parsing with nested structure and JSONPath
- **Tests**: JSONPath navigation ($.data), boolean detection, date detection
- **Expected output**: expected/employees_types.json documents expected column types
- **Usage**: `cc-excel from-json input/employees.json -o output/employees.xlsx --json-path data`

### input/report-tables.md
- **Purpose**: Test Markdown pipe table extraction
- **Tests**: Multiple table detection, --all-tables flag, --table-index selection
- **Expected output**: expected/report_tables_count.json documents expected tables and columns

### input/semicolon.csv
- **Purpose**: Test custom delimiter handling
- **Tests**: Semicolon delimiter, boolean type inference (yes/no)
- **Usage**: `cc-excel from-csv input/semicolon.csv -o output/semicolon.xlsx --delimiter ";"`

## Running Tests

```bash
# Unit tests (in-memory, no file I/O)
cd src/cc-excel
python -m pytest tests/ -v

# Fixture-based integration test
cc-excel from-csv test/fixtures/input/sales.csv -o test/fixtures/output/sales.xlsx --theme boardroom
cc-excel from-json test/fixtures/input/employees.json -o test/fixtures/output/employees.xlsx --json-path data
cc-excel from-markdown test/fixtures/input/report-tables.md -o test/fixtures/output/report.xlsx --all-tables
```

## Updating Expected Output

When tool behavior changes intentionally:
1. Run the tool against input files
2. Verify output opens correctly in Excel
3. Update expected/ JSON files with any type inference changes
4. Update this README

## Last Validated
Date: 2026-02-26
Tool Version: 0.1.0
