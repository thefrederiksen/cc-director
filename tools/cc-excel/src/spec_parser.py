"""Parse and validate JSON workbook specification files."""

import json
from pathlib import Path
from typing import Optional, Union

try:
    from .spec_models import (
        CellSpec,
        ConditionalFormatSpec,
        DataValidationSpec,
        RowSpec,
        SheetSpec,
        StyleType,
        WorkbookSpec,
    )
except ImportError:
    from src.spec_models import (
        CellSpec,
        ConditionalFormatSpec,
        DataValidationSpec,
        RowSpec,
        SheetSpec,
        StyleType,
        WorkbookSpec,
    )


_VALID_STYLES = {s.value for s in StyleType}


def parse_spec(path: Path) -> WorkbookSpec:
    """Parse a JSON workbook spec file into a WorkbookSpec object.

    Args:
        path: Path to the JSON spec file.

    Returns:
        Parsed WorkbookSpec object.

    Raises:
        FileNotFoundError: If the file does not exist.
        ValueError: If the JSON is malformed or the spec is invalid.
    """
    path = Path(path)
    if not path.exists():
        raise FileNotFoundError(f"Spec file not found: {path}")

    text = path.read_text(encoding="utf-8")
    try:
        data = json.loads(text)
    except json.JSONDecodeError as e:
        raise ValueError(f"Invalid JSON in spec file: {e}")

    if not isinstance(data, dict):
        raise ValueError("Spec file must contain a JSON object at the top level")

    return _parse_workbook(data)


def _parse_workbook(data: dict) -> WorkbookSpec:
    """Parse the top-level workbook spec."""
    theme = data.get("theme")
    if theme is not None and not isinstance(theme, str):
        raise ValueError("'theme' must be a string")

    named_ranges = data.get("named_ranges", {})
    if not isinstance(named_ranges, dict):
        raise ValueError("'named_ranges' must be an object")
    for key, val in named_ranges.items():
        if not isinstance(val, str):
            raise ValueError(f"Named range '{key}' must map to a string, got {type(val).__name__}")

    raw_sheets = data.get("sheets")
    if raw_sheets is None:
        raise ValueError("Spec must contain a 'sheets' array")
    if not isinstance(raw_sheets, list):
        raise ValueError("'sheets' must be an array")
    if len(raw_sheets) == 0:
        raise ValueError("'sheets' array must contain at least one sheet")

    sheets = []
    for i, raw_sheet in enumerate(raw_sheets):
        if not isinstance(raw_sheet, dict):
            raise ValueError(f"Sheet {i}: must be an object")
        sheets.append(_parse_sheet(raw_sheet, i))

    return WorkbookSpec(
        sheets=sheets,
        theme=theme,
        named_ranges=named_ranges,
    )


def _parse_sheet(data: dict, sheet_idx: int) -> SheetSpec:
    """Parse a single sheet spec."""
    prefix = f"Sheet {sheet_idx}"

    name = data.get("name")
    if not name or not isinstance(name, str):
        raise ValueError(f"{prefix}: 'name' is required and must be a string")

    prefix = f"Sheet '{name}'"

    # Columns (widths)
    columns = data.get("columns")
    if columns is not None:
        if not isinstance(columns, list):
            raise ValueError(f"{prefix}: 'columns' must be an array of numbers")
        for j, w in enumerate(columns):
            if not isinstance(w, (int, float)):
                raise ValueError(f"{prefix}: column width at index {j} must be a number")

    # Freeze panes
    freeze = data.get("freeze")
    if freeze is not None:
        if not isinstance(freeze, list) or len(freeze) != 2:
            raise ValueError(f"{prefix}: 'freeze' must be [row, col]")
        if not all(isinstance(v, int) for v in freeze):
            raise ValueError(f"{prefix}: 'freeze' values must be integers")

    # Autofilter
    autofilter = data.get("autofilter")

    # Rows
    raw_rows = data.get("rows", [])
    if not isinstance(raw_rows, list):
        raise ValueError(f"{prefix}: 'rows' must be an array")
    rows = []
    for r_idx, raw_row in enumerate(raw_rows):
        rows.append(_parse_row(raw_row, prefix, r_idx))

    # Conditional formats
    raw_cfs = data.get("conditional_formats", [])
    if not isinstance(raw_cfs, list):
        raise ValueError(f"{prefix}: 'conditional_formats' must be an array")
    cond_formats = [_parse_conditional_format(cf, prefix, i) for i, cf in enumerate(raw_cfs)]

    # Data validations
    raw_dvs = data.get("data_validation", [])
    if not isinstance(raw_dvs, list):
        raise ValueError(f"{prefix}: 'data_validation' must be an array")
    data_validations = [_parse_data_validation(dv, prefix, i) for i, dv in enumerate(raw_dvs)]

    return SheetSpec(
        name=name,
        rows=rows,
        columns=columns,
        freeze=freeze,
        autofilter=autofilter,
        conditional_formats=cond_formats,
        data_validations=data_validations,
    )


def _parse_style(value: Optional[str], context: str) -> Optional[StyleType]:
    """Parse a style string into a StyleType enum value."""
    if value is None:
        return None
    if not isinstance(value, str):
        raise ValueError(f"{context}: 'style' must be a string")
    if value not in _VALID_STYLES:
        valid = ", ".join(sorted(_VALID_STYLES))
        raise ValueError(f"{context}: unknown style '{value}'. Valid styles: {valid}")
    return StyleType(value)


def _parse_row(raw: object, sheet_prefix: str, row_idx: int) -> Optional[RowSpec]:
    """Parse a single row spec."""
    ctx = f"{sheet_prefix}, row {row_idx}"

    # null = spacer row
    if raw is None:
        return None

    if not isinstance(raw, dict):
        raise ValueError(f"{ctx}: row must be an object or null")

    style = _parse_style(raw.get("style"), ctx)
    merge = raw.get("merge", 0)
    value = raw.get("value")
    height = raw.get("height")

    if height is not None and not isinstance(height, (int, float)):
        raise ValueError(f"{ctx}: 'height' must be a number")

    # Merged title row (shorthand)
    if merge and value is not None:
        if not isinstance(merge, int) or merge < 1:
            raise ValueError(f"{ctx}: 'merge' must be a positive integer")
        return RowSpec(
            style=style,
            merge=merge,
            value=value,
            height=height,
        )

    # Data row with cells
    raw_cells = raw.get("cells")
    if raw_cells is not None:
        if not isinstance(raw_cells, list):
            raise ValueError(f"{ctx}: 'cells' must be an array")
        cells = [_parse_cell(c, ctx, i) for i, c in enumerate(raw_cells)]
        return RowSpec(
            cells=cells,
            style=style,
            height=height,
        )

    # Row with only a style and no cells/merge -- treat as empty
    return RowSpec(style=style, height=height)


def _parse_cell(raw: object, row_ctx: str, cell_idx: int) -> Union[CellSpec, object]:
    """Parse a single cell value.

    Simple literals (str, int, float, bool, None) are returned as-is.
    Objects with special keys (v, f, fmt, style, merge, comment) are
    converted to CellSpec.
    """
    ctx = f"{row_ctx}, cell {cell_idx}"

    # Primitives pass through
    if raw is None or isinstance(raw, (str, int, float, bool)):
        return raw

    if not isinstance(raw, dict):
        raise ValueError(f"{ctx}: cell must be a value, object, or null")

    return CellSpec(
        value=raw.get("v"),
        formula=raw.get("f"),
        number_format=raw.get("fmt"),
        style=_parse_style(raw.get("style"), ctx),
        merge=raw.get("merge", 0),
        comment=raw.get("comment"),
    )


def _parse_conditional_format(data: dict, sheet_prefix: str, idx: int) -> ConditionalFormatSpec:
    """Parse a conditional format rule."""
    ctx = f"{sheet_prefix}, conditional_format {idx}"

    if not isinstance(data, dict):
        raise ValueError(f"{ctx}: must be an object")

    cf_range = data.get("range")
    if not cf_range or not isinstance(cf_range, str):
        raise ValueError(f"{ctx}: 'range' is required and must be a string")

    cf_type = data.get("type")
    if not cf_type or not isinstance(cf_type, str):
        raise ValueError(f"{ctx}: 'type' is required and must be a string")

    return ConditionalFormatSpec(
        range=cf_range,
        type=cf_type,
        criteria=data.get("criteria"),
        value=data.get("value"),
        min_color=data.get("min_color"),
        mid_color=data.get("mid_color"),
        max_color=data.get("max_color"),
        bg_color=data.get("bg_color"),
        font_color=data.get("font_color"),
        format_properties=data.get("format"),
    )


def _parse_data_validation(data: dict, sheet_prefix: str, idx: int) -> DataValidationSpec:
    """Parse a data validation rule."""
    ctx = f"{sheet_prefix}, data_validation {idx}"

    if not isinstance(data, dict):
        raise ValueError(f"{ctx}: must be an object")

    dv_range = data.get("range")
    if not dv_range or not isinstance(dv_range, str):
        raise ValueError(f"{ctx}: 'range' is required and must be a string")

    dv_type = data.get("type")
    if not dv_type or not isinstance(dv_type, str):
        raise ValueError(f"{ctx}: 'type' is required and must be a string")

    return DataValidationSpec(
        range=dv_range,
        type=dv_type,
        criteria=data.get("criteria"),
        value=data.get("value"),
        minimum=data.get("minimum"),
        maximum=data.get("maximum"),
        source=data.get("source"),
        input_title=data.get("input_title"),
        input_message=data.get("input_message"),
        error_title=data.get("error_title"),
        error_message=data.get("error_message"),
    )
