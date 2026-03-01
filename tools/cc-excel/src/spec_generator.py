"""Generate XLSX workbooks from a WorkbookSpec.

This module takes a parsed WorkbookSpec (from spec_parser) and produces
a fully formatted Excel workbook with formulas, merged cells, named ranges,
conditional formatting, and theme-derived styles.
"""

from pathlib import Path
from typing import Optional

import xlsxwriter

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
    from .themes import ExcelTheme
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
    from src.themes import ExcelTheme


# Border style mapping (same as xlsx_generator)
_BORDER_MAP = {
    "thin": 1,
    "medium": 2,
    "none": 0,
}


def generate_from_spec(
    spec: WorkbookSpec,
    theme: ExcelTheme,
    output_path: Path,
) -> None:
    """Generate a formatted .xlsx workbook from a WorkbookSpec.

    Args:
        spec: Parsed workbook specification.
        theme: ExcelTheme to apply for style generation.
        output_path: Path for the output .xlsx file.
    """
    output_path = Path(output_path)
    workbook = xlsxwriter.Workbook(str(output_path))

    try:
        formats = _build_style_formats(workbook, theme)

        for sheet_spec in spec.sheets:
            _write_spec_sheet(workbook, sheet_spec, theme, formats)

        # Define named ranges
        for range_name, range_ref in spec.named_ranges.items():
            workbook.define_name(range_name, f"={range_ref}")

    finally:
        workbook.close()


def _build_style_formats(
    workbook: xlsxwriter.Workbook,
    theme: ExcelTheme,
) -> dict:
    """Build the set of named style formats derived from the theme.

    Returns a dict with format objects keyed by style name, plus a '_props'
    dict mapping style names to their raw property dicts (for creating
    number-format variants).
    """
    border = _BORDER_MAP.get(theme.border_style, 1)

    base_props = {
        "font_name": theme.fonts.body,
        "font_size": theme.fonts.body_size,
        "font_color": theme.colors.text,
        "border": border,
        "border_color": theme.colors.border,
        "valign": "vcenter",
    }

    # Define complete property dicts for each style
    style_props = {
        "header": {
            "bg_color": theme.colors.header_bg,
            "font_color": theme.colors.header_text,
            "font_name": theme.fonts.header,
            "font_size": theme.fonts.header_size,
            "bold": True,
            "border": border,
            "border_color": theme.colors.border,
            "text_wrap": True,
            "valign": "vcenter",
        },
        "subheader": {
            **base_props,
            "bg_color": theme.colors.alt_row_bg,
            "bold": True,
            "font_size": theme.fonts.header_size,
        },
        "title": {
            "font_name": theme.fonts.header,
            "font_size": theme.fonts.header_size + 4,
            "font_color": theme.colors.header_bg,
            "bold": True,
            "border": 0,
            "valign": "vcenter",
        },
        "subtitle": {
            "font_name": theme.fonts.body,
            "font_size": theme.fonts.body_size,
            "font_color": theme.colors.border,
            "italic": True,
            "border": 0,
            "valign": "vcenter",
        },
        "input": {
            **base_props,
            "bg_color": "#FFFFCC",
            "border": 1,
            "border_color": "#E6A800",
        },
        "total": {
            **base_props,
            "bold": True,
            "bg_color": theme.colors.alt_row_bg,
            "top": 2,
            "bottom": 2,
            "top_color": theme.colors.header_bg,
            "bottom_color": theme.colors.header_bg,
        },
        "best": {
            **base_props,
            "bg_color": "#C6EFCE",
            "font_color": "#006100",
        },
        "worst": {
            **base_props,
            "bg_color": "#FFC7CE",
            "font_color": "#9C0006",
        },
        "accent": {
            **base_props,
            "bg_color": theme.colors.accent,
            "font_color": "#FFFFFF",
        },
        "body": {**base_props},
    }

    # Create format objects from property dicts
    result = {"_props": style_props}
    for name, props in style_props.items():
        result[name] = workbook.add_format(props)

    return result


def _get_cell_format(
    workbook: xlsxwriter.Workbook,
    formats: dict,
    row_style: Optional[StyleType],
    cell_style: Optional[StyleType],
    number_format: Optional[str],
) -> xlsxwriter.format.Format:
    """Resolve the format for a cell based on row style, cell style, and number format.

    Priority: cell style > row style > body default.
    Number format is applied as an overlay on whatever style is selected.
    """
    # Determine the base style
    style = cell_style or row_style
    style_key = style.value if style else "body"
    base_fmt = formats.get(style_key, formats["body"])

    # If no number format override, return the pre-built format
    if not number_format:
        return base_fmt

    # Clone the style's property dict and add the number format
    props = dict(formats["_props"].get(style_key, formats["_props"]["body"]))
    props["num_format"] = number_format
    return workbook.add_format(props)


def _write_spec_sheet(
    workbook: xlsxwriter.Workbook,
    sheet_spec: SheetSpec,
    theme: ExcelTheme,
    formats: dict,
) -> None:
    """Write a single SheetSpec to a worksheet."""
    sheet_name = sheet_spec.name[:31]  # Excel 31-char limit
    worksheet = workbook.add_worksheet(sheet_name)

    # Set column widths
    if sheet_spec.columns:
        for col_idx, width in enumerate(sheet_spec.columns):
            worksheet.set_column(col_idx, col_idx, width)

    # Write all rows
    _write_spec_rows(workbook, worksheet, sheet_spec, formats)

    # Apply sheet-level settings
    _apply_sheet_settings(workbook, worksheet, sheet_spec)


def _write_spec_rows(
    workbook: xlsxwriter.Workbook,
    worksheet: xlsxwriter.worksheet.Worksheet,
    sheet_spec: SheetSpec,
    formats: dict,
) -> None:
    """Write all rows from a SheetSpec to the worksheet."""
    excel_row = 0
    for row_spec in sheet_spec.rows:
        if row_spec is None:
            excel_row += 1
            continue

        if row_spec.height:
            worksheet.set_row(excel_row, row_spec.height)

        # Merged title row (shorthand)
        if row_spec.merge and row_spec.value is not None:
            style = row_spec.style or StyleType.TITLE
            fmt = formats.get(style.value, formats["title"])
            last_col = row_spec.merge - 1
            if last_col > 0:
                worksheet.merge_range(
                    excel_row, 0, excel_row, last_col,
                    row_spec.value, fmt,
                )
            else:
                worksheet.write(excel_row, 0, row_spec.value, fmt)
            excel_row += 1
            continue

        # Data row with cells
        if row_spec.cells is not None:
            col_idx = 0
            for cell in row_spec.cells:
                col_idx = _write_cell(
                    workbook, worksheet, formats,
                    excel_row, col_idx,
                    cell, row_spec.style,
                )
            excel_row += 1
            continue

        # Empty styled row (no cells, no merge)
        excel_row += 1


def _apply_sheet_settings(
    workbook: xlsxwriter.Workbook,
    worksheet: xlsxwriter.worksheet.Worksheet,
    sheet_spec: SheetSpec,
) -> None:
    """Apply freeze panes, autofilter, conditional formats, and data validation."""
    if sheet_spec.freeze:
        worksheet.freeze_panes(sheet_spec.freeze[0], sheet_spec.freeze[1])

    if sheet_spec.autofilter is True:
        num_rows = len(sheet_spec.rows)
        max_cols = _get_max_columns(sheet_spec)
        if num_rows > 0 and max_cols > 0:
            worksheet.autofilter(0, 0, num_rows - 1, max_cols - 1)
    elif isinstance(sheet_spec.autofilter, list) and len(sheet_spec.autofilter) == 4:
        worksheet.autofilter(*sheet_spec.autofilter)

    for cf in sheet_spec.conditional_formats:
        _apply_conditional_format(workbook, worksheet, cf)

    for dv in sheet_spec.data_validations:
        _apply_data_validation(worksheet, dv)


def _write_cell(
    workbook: xlsxwriter.Workbook,
    worksheet: xlsxwriter.worksheet.Worksheet,
    formats: dict,
    row: int,
    col: int,
    cell: object,
    row_style: Optional[StyleType],
) -> int:
    """Write a single cell and return the next column index.

    Handles literals, CellSpec objects, formulas, merges, and comments.
    """
    # Simple literal values
    if not isinstance(cell, CellSpec):
        fmt = _get_cell_format(workbook, formats, row_style, None, None)
        _write_value(worksheet, row, col, cell, fmt)
        return col + 1

    # CellSpec object
    cell_fmt = _get_cell_format(
        workbook, formats,
        row_style, cell.style, cell.number_format,
    )

    # Cell-level merge
    if cell.merge > 1:
        last_col = col + cell.merge - 1
        content = cell.formula or cell.value or ""
        if cell.formula:
            worksheet.merge_range(row, col, row, last_col, "", cell_fmt)
            worksheet.write_formula(row, col, cell.formula, cell_fmt, cell.value)
        else:
            worksheet.merge_range(row, col, row, last_col, content, cell_fmt)
        # Add comment if present
        if cell.comment:
            worksheet.write_comment(row, col, cell.comment)
        return col + cell.merge

    # Formula cell
    if cell.formula:
        worksheet.write_formula(row, col, cell.formula, cell_fmt, cell.value)
    else:
        _write_value(worksheet, row, col, cell.value, cell_fmt)

    # Comment
    if cell.comment:
        worksheet.write_comment(row, col, cell.comment)

    return col + 1


def _write_value(
    worksheet: xlsxwriter.worksheet.Worksheet,
    row: int,
    col: int,
    value: object,
    fmt: xlsxwriter.format.Format,
) -> None:
    """Write a typed value to a cell."""
    if value is None:
        worksheet.write_blank(row, col, None, fmt)
    elif isinstance(value, bool):
        worksheet.write_boolean(row, col, value, fmt)
    elif isinstance(value, (int, float)):
        worksheet.write_number(row, col, value, fmt)
    elif isinstance(value, str):
        worksheet.write_string(row, col, value, fmt)
    else:
        worksheet.write(row, col, value, fmt)


def _get_max_columns(sheet_spec: SheetSpec) -> int:
    """Get the maximum number of columns across all rows in a sheet."""
    max_cols = 0
    for row in sheet_spec.rows:
        if row is None:
            continue
        if row.merge:
            max_cols = max(max_cols, row.merge)
        if row.cells:
            # Count actual columns including cell-level merges
            col_count = 0
            for cell in row.cells:
                if isinstance(cell, CellSpec) and cell.merge > 1:
                    col_count += cell.merge
                else:
                    col_count += 1
            max_cols = max(max_cols, col_count)
    return max_cols


def _apply_conditional_format(
    workbook: xlsxwriter.Workbook,
    worksheet: xlsxwriter.worksheet.Worksheet,
    cf: ConditionalFormatSpec,
) -> None:
    """Apply a conditional format rule to a worksheet."""
    options = {"type": cf.type}

    if cf.criteria:
        options["criteria"] = cf.criteria
    if cf.value is not None:
        options["value"] = cf.value

    # Color scale types
    if cf.type in ("2_color_scale", "color_scale"):
        options["type"] = "2_color_scale"
        if cf.min_color:
            options["min_color"] = cf.min_color
        if cf.max_color:
            options["max_color"] = cf.max_color
    elif cf.type == "3_color_scale":
        if cf.min_color:
            options["min_color"] = cf.min_color
        if cf.mid_color:
            options["mid_color"] = cf.mid_color
        if cf.max_color:
            options["max_color"] = cf.max_color

    # Cell-type conditional format with custom formatting
    if cf.bg_color or cf.font_color or cf.format_properties:
        fmt_props = {}
        if cf.bg_color:
            fmt_props["bg_color"] = cf.bg_color
        if cf.font_color:
            fmt_props["font_color"] = cf.font_color
        if cf.format_properties:
            fmt_props.update(cf.format_properties)
        options["format"] = workbook.add_format(fmt_props)

    worksheet.conditional_format(cf.range, options)


def _apply_data_validation(
    worksheet: xlsxwriter.worksheet.Worksheet,
    dv: DataValidationSpec,
) -> None:
    """Apply a data validation rule to a worksheet."""
    options = {"validate": dv.type}

    if dv.criteria:
        options["criteria"] = dv.criteria
    if dv.value is not None:
        options["value"] = dv.value
    if dv.minimum is not None:
        options["minimum"] = dv.minimum
    if dv.maximum is not None:
        options["maximum"] = dv.maximum
    if dv.source:
        options["source"] = dv.source
    if dv.input_title:
        options["input_title"] = dv.input_title
    if dv.input_message:
        options["input_message"] = dv.input_message
    if dv.error_title:
        options["error_title"] = dv.error_title
    if dv.error_message:
        options["error_message"] = dv.error_message

    worksheet.data_validation(dv.range, options)
