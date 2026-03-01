"""JSON parser for cc-excel."""

import json
from pathlib import Path
from typing import Optional

try:
    from ..models import SheetData, ColumnInfo
except ImportError:
    from src.models import SheetData, ColumnInfo


def parse_json(
    path: Path,
    json_path: Optional[str] = None,
) -> SheetData:
    """Parse a JSON file into SheetData.

    Supports two formats:
    1. Array of objects: [{"name": "Alice", "score": 95}, ...]
    2. Array of arrays with header row: [["name", "score"], ["Alice", 95], ...]

    Args:
        path: Path to JSON file.
        json_path: Dot-path to locate the array (e.g. "data" or "results.items").

    Returns:
        SheetData with columns and rows populated.
    """
    path = Path(path)
    if not path.exists():
        raise FileNotFoundError(f"JSON file not found: {path}")

    with open(path, encoding="utf-8") as f:
        data = json.load(f)

    # Navigate to the target array using json_path
    if json_path:
        data = _navigate_path(data, json_path)

    if not isinstance(data, list):
        raise ValueError(
            f"Expected a JSON array, got {type(data).__name__}. "
            "Use --json-path to specify the path to the data array."
        )

    if not data:
        raise ValueError("JSON array is empty.")

    # Detect format: array of objects vs array of arrays
    first = data[0]

    if isinstance(first, dict):
        return _parse_objects(data, path)
    elif isinstance(first, list):
        return _parse_arrays(data, path)
    else:
        raise ValueError(
            f"Expected array of objects or array of arrays, "
            f"got array of {type(first).__name__}."
        )


def _navigate_path(data: object, path: str) -> object:
    """Navigate a dot-separated path through nested dicts.

    Strips leading "$." for JSONPath-style paths.
    """
    path = path.strip()
    if path.startswith("$."):
        path = path[2:]
    elif path.startswith("$"):
        path = path[1:]

    if not path:
        return data

    parts = path.split(".")
    current = data
    for part in parts:
        if isinstance(current, dict):
            if part not in current:
                available = ", ".join(current.keys())
                raise ValueError(
                    f"Key '{part}' not found. Available keys: {available}"
                )
            current = current[part]
        elif isinstance(current, list):
            try:
                index = int(part)
                current = current[index]
            except (ValueError, IndexError):
                raise ValueError(
                    f"Cannot navigate '{part}' in a list. Use an integer index."
                )
        else:
            raise ValueError(
                f"Cannot navigate '{part}' in {type(current).__name__}."
            )
    return current


def _parse_objects(data: list[dict], path: Path) -> SheetData:
    """Parse array of objects format."""
    # Collect all unique keys in order of first appearance
    seen_keys: dict[str, None] = {}
    for item in data:
        for key in item.keys():
            if key not in seen_keys:
                seen_keys[key] = None

    headers = list(seen_keys.keys())
    columns = [ColumnInfo(name=h) for h in headers]

    rows = []
    for item in data:
        row = [str(item.get(h, "")) if item.get(h) is not None else "" for h in headers]
        rows.append(row)

    return SheetData(
        title=path.stem,
        columns=columns,
        rows=rows,
        source_file=str(path),
    )


def _parse_arrays(data: list[list], path: Path) -> SheetData:
    """Parse array of arrays format (first row = headers)."""
    header_row = data[0]
    columns = [ColumnInfo(name=str(h).strip()) for h in header_row]

    rows = []
    num_cols = len(columns)
    for row in data[1:]:
        str_row = [str(v) if v is not None else "" for v in row]
        # Normalize length
        if len(str_row) < num_cols:
            str_row = str_row + [""] * (num_cols - len(str_row))
        elif len(str_row) > num_cols:
            str_row = str_row[:num_cols]
        rows.append(str_row)

    return SheetData(
        title=path.stem,
        columns=columns,
        rows=rows,
        source_file=str(path),
    )
