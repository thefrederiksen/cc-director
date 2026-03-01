"""Type inference engine for cc-excel.

Scans column values to detect data types and assign Excel number formats.
"""

import re
from datetime import datetime
from typing import Optional

try:
    from .models import SheetData, ColumnInfo, ColumnType
except ImportError:
    from src.models import SheetData, ColumnInfo, ColumnType


# Patterns for type detection
_INT_RE = re.compile(r"^-?\d{1,15}$")
_FLOAT_RE = re.compile(r"^-?\d{1,15}\.\d+$")
_PERCENTAGE_RE = re.compile(r"^-?\d+\.?\d*\s*%$")
_CURRENCY_RE = re.compile(r"^[\$\u00a3\u20ac]\s*-?\d[\d,]*\.?\d*$|^-?\d[\d,]*\.?\d*\s*[\$\u00a3\u20ac]$")
_BOOLEAN_RE = re.compile(r"^(true|false|yes|no|1|0)$", re.IGNORECASE)

# Common date formats to try
_DATE_FORMATS = [
    "%Y-%m-%d",
    "%m/%d/%Y",
    "%d/%m/%Y",
    "%Y-%m-%dT%H:%M:%S",
    "%Y-%m-%d %H:%M:%S",
    "%m/%d/%y",
    "%d-%b-%Y",
    "%b %d, %Y",
]

# Excel number format strings per type
_FORMAT_MAP = {
    ColumnType.INTEGER: "#,##0",
    ColumnType.FLOAT: "#,##0.00",
    ColumnType.PERCENTAGE: "0.0%",
    ColumnType.CURRENCY: "$#,##0.00",
    ColumnType.DATE: "yyyy-mm-dd",
    ColumnType.BOOLEAN: "",
    ColumnType.TEXT: "",
}

# Minimum width for columns
_MIN_WIDTH = 8.0
_MAX_WIDTH = 50.0
_HEADER_PADDING = 2.0


def infer_types(sheet: SheetData) -> SheetData:
    """Detect column types and convert values in-place.

    For each column, samples all non-empty values to determine the majority type.
    Converts string values to their native Python types where possible.
    Sets number_format and width on each ColumnInfo.

    Returns the same SheetData with mutated columns and rows.
    """
    if not sheet.columns or not sheet.rows:
        return sheet

    num_cols = len(sheet.columns)

    for col_idx in range(num_cols):
        values = [row[col_idx] for row in sheet.rows if col_idx < len(row)]
        non_empty = [v for v in values if v is not None and str(v).strip() != ""]

        if not non_empty:
            sheet.columns[col_idx].col_type = ColumnType.TEXT
            sheet.columns[col_idx].number_format = ""
            sheet.columns[col_idx].width = max(
                _MIN_WIDTH, len(sheet.columns[col_idx].name) + _HEADER_PADDING
            )
            continue

        col_type = _detect_column_type(non_empty)
        sheet.columns[col_idx].col_type = col_type
        sheet.columns[col_idx].number_format = _FORMAT_MAP[col_type]

        # Convert values and calculate width
        max_content_width = len(sheet.columns[col_idx].name) + _HEADER_PADDING

        for row in sheet.rows:
            if col_idx < len(row):
                original = row[col_idx]
                converted = _convert_value(original, col_type)
                row[col_idx] = converted

                # Calculate display width
                display_str = str(original) if original is not None else ""
                max_content_width = max(max_content_width, len(display_str) + 1)

        sheet.columns[col_idx].width = min(max(max_content_width, _MIN_WIDTH), _MAX_WIDTH)

    return sheet


def _detect_column_type(values: list) -> ColumnType:
    """Detect the majority type for a list of non-empty values."""
    type_counts = {t: 0 for t in ColumnType}

    for v in values:
        s = str(v).strip()
        detected = _detect_single_value(s)
        type_counts[detected] += 1

    # Find the type with the most matches (excluding TEXT as fallback)
    total = len(values)
    best_type = ColumnType.TEXT
    best_count = 0

    for col_type in [
        ColumnType.DATE,
        ColumnType.CURRENCY,
        ColumnType.PERCENTAGE,
        ColumnType.FLOAT,
        ColumnType.INTEGER,
        ColumnType.BOOLEAN,
    ]:
        count = type_counts[col_type]
        # Require majority (>50%) to assign a non-text type
        if count > total * 0.5 and count > best_count:
            best_type = col_type
            best_count = count

    return best_type


def _detect_single_value(s: str) -> ColumnType:
    """Detect the type of a single string value."""
    if not s:
        return ColumnType.TEXT

    # Check percentage first (before float, since "50.0%" contains a float)
    if _PERCENTAGE_RE.match(s):
        return ColumnType.PERCENTAGE

    # Check currency
    if _CURRENCY_RE.match(s):
        return ColumnType.CURRENCY

    # Check boolean
    if _BOOLEAN_RE.match(s):
        return ColumnType.BOOLEAN

    # Check integer
    if _INT_RE.match(s):
        return ColumnType.INTEGER

    # Check float
    s_no_commas = s.replace(",", "")
    if _FLOAT_RE.match(s_no_commas):
        return ColumnType.FLOAT

    # Also check if original matches float with commas (e.g., "1,234.56")
    if re.match(r"^-?\d{1,3}(,\d{3})*\.\d+$", s):
        return ColumnType.FLOAT

    # Check integer with commas (e.g., "1,234")
    if re.match(r"^-?\d{1,3}(,\d{3})+$", s):
        return ColumnType.INTEGER

    # Check date
    if _is_date(s):
        return ColumnType.DATE

    return ColumnType.TEXT


def _is_date(s: str) -> bool:
    """Check if a string parses as a date."""
    for fmt in _DATE_FORMATS:
        try:
            datetime.strptime(s, fmt)
            return True
        except ValueError:
            continue
    return False


def _convert_value(value: object, col_type: ColumnType) -> object:
    """Convert a string value to its native Python type."""
    if value is None:
        return None

    s = str(value).strip()
    if not s:
        return ""

    if col_type == ColumnType.INTEGER:
        try:
            return int(s.replace(",", ""))
        except ValueError:
            return s

    elif col_type == ColumnType.FLOAT:
        try:
            return float(s.replace(",", ""))
        except ValueError:
            return s

    elif col_type == ColumnType.PERCENTAGE:
        try:
            # Remove % and convert to decimal (Excel stores percentages as decimals)
            num_str = s.rstrip("%").strip()
            return float(num_str) / 100.0
        except ValueError:
            return s

    elif col_type == ColumnType.CURRENCY:
        try:
            # Remove currency symbols and commas
            cleaned = re.sub(r"[\$\u00a3\u20ac,\s]", "", s)
            return float(cleaned)
        except ValueError:
            return s

    elif col_type == ColumnType.DATE:
        for fmt in _DATE_FORMATS:
            try:
                return datetime.strptime(s, fmt)
            except ValueError:
                continue
        return s

    elif col_type == ColumnType.BOOLEAN:
        lower = s.lower()
        if lower in ("true", "yes", "1"):
            return True
        elif lower in ("false", "no", "0"):
            return False
        return s

    return s
