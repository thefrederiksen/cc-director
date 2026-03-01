"""Markdown table parser for cc-excel."""

from pathlib import Path
from typing import Optional

from markdown_it import MarkdownIt

try:
    from ..models import SheetData, ColumnInfo
except ImportError:
    from src.models import SheetData, ColumnInfo


def parse_markdown_tables(
    path: Path,
    table_index: int = 0,
    all_tables: bool = False,
) -> list[SheetData]:
    """Parse Markdown pipe tables from a file.

    Args:
        path: Path to Markdown file.
        table_index: Which table to extract (0-based) when not using all_tables.
        all_tables: Extract all tables as separate SheetData entries.

    Returns:
        List of SheetData (one per table if all_tables, else single-element list).
    """
    path = Path(path)
    if not path.exists():
        raise FileNotFoundError(f"Markdown file not found: {path}")

    content = path.read_text(encoding="utf-8")

    md = MarkdownIt().enable("table")
    tokens = md.parse(content)

    tables = _extract_tables(tokens)

    if not tables:
        raise ValueError(f"No pipe tables found in: {path}")

    if all_tables:
        sheets = []
        for i, (headers, rows) in enumerate(tables):
            title = f"Table {i + 1}" if len(tables) > 1 else "Table"
            sheets.append(_to_sheet_data(title, headers, rows, path))
        return sheets
    else:
        if table_index >= len(tables):
            raise ValueError(
                f"Table index {table_index} out of range. "
                f"File contains {len(tables)} table(s) (0-based indexing)."
            )
        headers, rows = tables[table_index]
        title = path.stem
        return [_to_sheet_data(title, headers, rows, path)]


def _extract_tables(tokens: list) -> list[tuple[list[str], list[list[str]]]]:
    """Extract table data from markdown-it tokens.

    Returns list of (headers, rows) tuples.
    """
    tables = []
    i = 0
    while i < len(tokens):
        token = tokens[i]
        if token.type == "table_open":
            headers, rows, i = _parse_table_tokens(tokens, i)
            tables.append((headers, rows))
        else:
            i += 1
    return tables


def _parse_table_tokens(
    tokens: list, start: int
) -> tuple[list[str], list[list[str]], int]:
    """Parse a single table from tokens starting at table_open.

    Returns (headers, rows, next_index).
    """
    headers = []
    rows = []
    current_row: list[str] = []
    in_thead = False
    in_tbody = False
    i = start + 1  # skip table_open

    while i < len(tokens):
        token = tokens[i]

        if token.type == "table_close":
            return headers, rows, i + 1

        elif token.type == "thead_open":
            in_thead = True
        elif token.type == "thead_close":
            in_thead = False
        elif token.type == "tbody_open":
            in_tbody = True
        elif token.type == "tbody_close":
            in_tbody = False

        elif token.type == "tr_open":
            current_row = []
        elif token.type == "tr_close":
            if in_thead:
                headers = current_row
            elif in_tbody:
                rows.append(current_row)

        elif token.type == "th_open" or token.type == "td_open":
            pass
        elif token.type == "th_close" or token.type == "td_close":
            pass

        elif token.type == "inline":
            cell_text = token.content.strip() if token.content else ""
            current_row.append(cell_text)

        i += 1

    return headers, rows, i


def _to_sheet_data(
    title: str,
    headers: list[str],
    rows: list[list[str]],
    path: Path,
) -> SheetData:
    """Convert parsed table data to SheetData."""
    columns = [ColumnInfo(name=h) for h in headers]
    num_cols = len(columns)

    # Normalize row lengths
    normalized_rows = []
    for row in rows:
        if len(row) < num_cols:
            row = row + [""] * (num_cols - len(row))
        elif len(row) > num_cols:
            row = row[:num_cols]
        normalized_rows.append(row)

    return SheetData(
        title=title,
        columns=columns,
        rows=normalized_rows,
        source_file=str(path),
    )
