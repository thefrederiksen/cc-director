"""CSV parser for cc-excel."""

import csv
from pathlib import Path
from typing import Optional

try:
    from ..models import SheetData, ColumnInfo
except ImportError:
    from src.models import SheetData, ColumnInfo


def parse_csv(
    path: Path,
    delimiter: str = ",",
    encoding: str = "utf-8",
    has_header: bool = True,
) -> SheetData:
    """Parse a CSV file into SheetData.

    Args:
        path: Path to CSV file.
        delimiter: Column delimiter character.
        encoding: File encoding.
        has_header: Whether the first row contains headers.

    Returns:
        SheetData with columns and rows populated.
    """
    path = Path(path)
    if not path.exists():
        raise FileNotFoundError(f"CSV file not found: {path}")

    with open(path, newline="", encoding=encoding) as f:
        reader = csv.reader(f, delimiter=delimiter)
        all_rows = list(reader)

    if not all_rows:
        raise ValueError(f"CSV file is empty: {path}")

    if has_header:
        header_row = all_rows[0]
        data_rows = all_rows[1:]
    else:
        # Auto-generate column names: A, B, C, ...
        num_cols = len(all_rows[0])
        header_row = [_column_letter(i) for i in range(num_cols)]
        data_rows = all_rows

    columns = [ColumnInfo(name=name.strip()) for name in header_row]

    # Normalize row lengths to match column count
    num_cols = len(columns)
    rows = []
    for row in data_rows:
        if len(row) < num_cols:
            row = row + [""] * (num_cols - len(row))
        elif len(row) > num_cols:
            row = row[:num_cols]
        rows.append(row)

    return SheetData(
        title=path.stem,
        columns=columns,
        rows=rows,
        source_file=str(path),
    )


def _column_letter(index: int) -> str:
    """Convert 0-based index to Excel-style column letter (A, B, ..., Z, AA, AB, ...)."""
    result = ""
    while True:
        result = chr(65 + index % 26) + result
        index = index // 26 - 1
        if index < 0:
            break
    return result
