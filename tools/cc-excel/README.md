# cc-excel

CLI tool that converts tabular data from CSV, JSON, and Markdown into professionally formatted Excel workbooks (.xlsx).

## What It Does

- Converts CSV, JSON, and Markdown pipe tables to .xlsx format
- Automatic type inference (integers, floats, percentages, currency, dates, booleans)
- 7 professional themes (boardroom, paper, terminal, spark, thesis, obsidian, blueprint)
- Chart generation (bar, line, pie, column)
- Autofilter, freeze panes, dynamic column widths
- JSONPath navigation for nested JSON structures
- Multi-table extraction from Markdown files

## What It Does NOT Do

- Does not read or modify existing Excel files
- Does not support formulas or macros
- Does not connect to databases or APIs
- Does not handle .xls (legacy) format - only .xlsx

## Installation

Built as part of cc-director suite:

```powershell
.\build.ps1
```

Requires Python 3.11+.

## Usage

### From CSV

```bash
cc-excel from-csv sales.csv -o sales.xlsx
cc-excel from-csv data.csv -o report.xlsx --theme boardroom
cc-excel from-csv data.csv -o report.xlsx --delimiter ";" --encoding utf-8
cc-excel from-csv data.csv -o report.xlsx --no-header
cc-excel from-csv data.csv -o report.xlsx --sheet-name "Q4 Sales"
```

### From JSON

```bash
cc-excel from-json api-response.json -o report.xlsx
cc-excel from-json data.json -o report.xlsx --theme terminal
cc-excel from-json nested.json -o report.xlsx --json-path "$.results"
```

### From Markdown

```bash
cc-excel from-markdown report.md -o report.xlsx
cc-excel from-markdown report.md -o report.xlsx --all-tables
cc-excel from-markdown report.md -o report.xlsx --table-index 2
```

### With Charts

```bash
cc-excel from-csv sales.csv -o chart.xlsx --chart bar --chart-x 0 --chart-y 1
cc-excel from-csv sales.csv -o chart.xlsx --chart line --chart-x Quarter --chart-y Revenue --chart-y Profit
```

### Utility

```bash
cc-excel --version
cc-excel --themes
```

## Options

### from-csv

| Option | Description | Default |
|--------|-------------|---------|
| `-o, --output` | Output .xlsx file path (required) | - |
| `-t, --theme` | Theme name | paper |
| `--delimiter` | CSV delimiter character | , |
| `--encoding` | File encoding | utf-8 |
| `--no-header` | First row is data, not headers | false |
| `--sheet-name` | Custom worksheet tab name | - |
| `--no-autofilter` | Disable autofilter on header row | false |
| `--no-freeze` | Disable freeze panes | false |
| `--chart` | Chart type: bar, line, pie, column | - |
| `--chart-x` | Column name/index for categories | - |
| `--chart-y` | Column name/index for values (repeatable) | - |

### from-json

| Option | Description | Default |
|--------|-------------|---------|
| `-o, --output` | Output .xlsx file path (required) | - |
| `-t, --theme` | Theme name | paper |
| `--json-path` | JSONPath to data array (e.g., '$.results') | - |
| `--sheet-name` | Custom worksheet tab name | - |
| `--no-autofilter` | Disable autofilter | false |
| `--no-freeze` | Disable freeze panes | false |
| `--chart` | Chart type | - |
| `--chart-x` | Category column | - |
| `--chart-y` | Value columns (repeatable) | - |

### from-markdown

| Option | Description | Default |
|--------|-------------|---------|
| `-o, --output` | Output .xlsx file path (required) | - |
| `-t, --theme` | Theme name | paper |
| `--table-index` | Which table to extract (0-based) | 0 |
| `--all-tables` | Extract all tables as separate sheets | false |
| `--sheet-name` | Custom worksheet tab name | - |
| `--no-autofilter` | Disable autofilter | false |
| `--no-freeze` | Disable freeze panes | false |

## Themes

| Theme | Description |
|-------|-------------|
| boardroom | Corporate, executive, serif fonts |
| paper | Minimal, clean, elegant (default) |
| terminal | Technical, monospace, dark header |
| spark | Creative, vibrant, colorful |
| thesis | Academic, traditional, scholarly |
| obsidian | Dark, modern, sleek |
| blueprint | Technical documentation, engineering |

## Type Inference

The tool automatically detects and formats column types:

- **Integer** - Format: #,##0
- **Float** - Format: #,##0.00
- **Percentage** - Format: 0.0% (supports 45%, 0.45)
- **Currency** - Format: $#,##0.00 (supports $, GBP, EUR)
- **Date** - Format: yyyy-mm-dd (8 date patterns)
- **Boolean** - true/false, yes/no, 1/0
- **Text** - Default fallback

Type is assigned when >50% of column values match that type.

## Dependencies

- Python 3.11+
- XlsxWriter >= 3.1.0
- markdown-it-py >= 3.0.0
- typer >= 0.9.0
- rich >= 13.0.0

## Testing

```bash
cd src/cc-excel
python -m pytest tests/ -v
```

6 test files covering CSV/JSON/Markdown parsing, type inference, xlsx generation, and chart building.
