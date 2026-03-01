"""XLSX generator for cc-excel.

Creates formatted Excel workbooks using XlsxWriter.
"""

from datetime import datetime
from pathlib import Path
from typing import Optional

import xlsxwriter

try:
    from .models import SheetData, ColumnType, ChartSpec, SummaryType, HighlightType
    from .themes import ExcelTheme
except ImportError:
    from src.models import SheetData, ColumnType, ChartSpec, SummaryType, HighlightType
    from src.themes import ExcelTheme


# Numeric column types that support summary formulas and highlighting
_NUMERIC_TYPES = {ColumnType.INTEGER, ColumnType.FLOAT, ColumnType.CURRENCY, ColumnType.PERCENTAGE}


# Border style mapping
_BORDER_MAP = {
    "thin": 1,
    "medium": 2,
    "none": 0,
}


def generate_xlsx(
    sheets: list[SheetData],
    theme: ExcelTheme,
    output_path: Path,
    autofilter: bool = True,
    freeze: bool = True,
    chart_spec: Optional[ChartSpec] = None,
    summary: Optional[SummaryType] = None,
    highlight: Optional[HighlightType] = None,
) -> None:
    """Generate a formatted .xlsx workbook.

    Args:
        sheets: List of SheetData to write (one per worksheet).
        theme: ExcelTheme to apply.
        output_path: Path for the output .xlsx file.
        autofilter: Enable autofilter on header row.
        freeze: Freeze the top row (header).
        chart_spec: Optional chart specification.
        summary: Optional summary rows to add (sum, avg, all).
        highlight: Optional conditional formatting (best-worst, scale).
    """
    output_path = Path(output_path)

    workbook = xlsxwriter.Workbook(str(output_path))

    try:
        # Build format objects from theme
        formats = _build_formats(workbook, theme)

        for sheet_data in sheets:
            _write_sheet(
                workbook, sheet_data, theme, formats,
                autofilter, freeze, summary, highlight,
            )

        # Add chart if specified
        if chart_spec and sheets:
            from .chart_builder import add_chart
            # Chart references the first sheet's data
            worksheet_name = sheets[0].title[:31]  # Excel 31-char limit
            worksheet = workbook.get_worksheet_by_name(worksheet_name)
            if worksheet:
                add_chart(workbook, worksheet, chart_spec, sheets[0], theme)
    finally:
        workbook.close()


def _build_formats(
    workbook: xlsxwriter.Workbook, theme: ExcelTheme
) -> dict[str, xlsxwriter.format.Format]:
    """Create all format objects needed for the workbook."""
    border = _BORDER_MAP.get(theme.border_style, 1)

    # Header format
    header_fmt = workbook.add_format({
        "bg_color": theme.colors.header_bg,
        "font_color": theme.colors.header_text,
        "font_name": theme.fonts.header,
        "font_size": theme.fonts.header_size,
        "bold": theme.header_bold,
        "border": border,
        "border_color": theme.colors.border,
        "text_wrap": True,
        "valign": "vcenter",
    })

    # Body format (odd rows)
    body_fmt = workbook.add_format({
        "font_color": theme.colors.text,
        "font_name": theme.fonts.body,
        "font_size": theme.fonts.body_size,
        "border": border,
        "border_color": theme.colors.border,
        "valign": "vcenter",
    })

    # Alternating row format (even rows)
    alt_row_fmt = workbook.add_format({
        "bg_color": theme.colors.alt_row_bg,
        "font_color": theme.colors.text,
        "font_name": theme.fonts.body,
        "font_size": theme.fonts.body_size,
        "border": border,
        "border_color": theme.colors.border,
        "valign": "vcenter",
    })

    return {
        "header": header_fmt,
        "body": body_fmt,
        "alt_row": alt_row_fmt,
        "border": border,
    }


def _get_typed_format(
    workbook: xlsxwriter.Workbook,
    theme: ExcelTheme,
    col_type: ColumnType,
    number_format: str,
    is_alt_row: bool,
) -> xlsxwriter.format.Format:
    """Create a format object with the correct number format for a column type."""
    border = _BORDER_MAP.get(theme.border_style, 1)

    props = {
        "font_color": theme.colors.text,
        "font_name": theme.fonts.body,
        "font_size": theme.fonts.body_size,
        "border": border,
        "border_color": theme.colors.border,
        "valign": "vcenter",
    }

    if is_alt_row and theme.alt_row_shading:
        props["bg_color"] = theme.colors.alt_row_bg

    if number_format:
        props["num_format"] = number_format

    return workbook.add_format(props)


def _write_sheet(
    workbook: xlsxwriter.Workbook,
    sheet_data: SheetData,
    theme: ExcelTheme,
    formats: dict,
    autofilter: bool,
    freeze: bool,
    summary: Optional[SummaryType] = None,
    highlight: Optional[HighlightType] = None,
) -> None:
    """Write a single SheetData to a worksheet."""
    # Excel sheet names: max 31 chars, no special chars
    sheet_name = sheet_data.title[:31]
    worksheet = workbook.add_worksheet(sheet_name)

    if not sheet_data.columns:
        return

    num_cols = len(sheet_data.columns)
    num_rows = len(sheet_data.rows)

    # Build typed format caches (one per column per row-parity)
    typed_formats: dict[tuple[int, bool], xlsxwriter.format.Format] = {}
    for col_idx, col in enumerate(sheet_data.columns):
        for is_alt in (False, True):
            key = (col_idx, is_alt)
            if col.number_format:
                typed_formats[key] = _get_typed_format(
                    workbook, theme, col.col_type, col.number_format, is_alt
                )
            else:
                typed_formats[key] = formats["alt_row"] if is_alt else formats["body"]

    # Write header row
    for col_idx, col in enumerate(sheet_data.columns):
        worksheet.write(0, col_idx, col.name, formats["header"])

    # Write data rows
    for row_idx, row in enumerate(sheet_data.rows):
        excel_row = row_idx + 1  # Row 0 is the header
        is_alt = (row_idx % 2 == 1) and theme.alt_row_shading

        for col_idx in range(num_cols):
            value = row[col_idx] if col_idx < len(row) else ""
            fmt = typed_formats[(col_idx, is_alt)]

            if isinstance(value, datetime):
                worksheet.write_datetime(excel_row, col_idx, value, fmt)
            elif isinstance(value, bool):
                worksheet.write_boolean(excel_row, col_idx, value, fmt)
            elif isinstance(value, (int, float)):
                worksheet.write_number(excel_row, col_idx, value, fmt)
            else:
                worksheet.write_string(excel_row, col_idx, str(value) if value is not None else "", fmt)

    # Set column widths
    for col_idx, col in enumerate(sheet_data.columns):
        worksheet.set_column(col_idx, col_idx, col.width)

    # Summary rows
    if summary and num_rows > 0:
        _write_summary_rows(workbook, worksheet, sheet_data, theme, formats, summary)

    # Conditional highlighting
    if highlight and num_rows > 0:
        _apply_highlight(workbook, worksheet, sheet_data, num_rows, highlight)

    # Autofilter (just data rows, before any summary rows)
    if autofilter and num_rows > 0:
        worksheet.autofilter(0, 0, num_rows, num_cols - 1)

    # Freeze panes (freeze header row)
    if freeze:
        worksheet.freeze_panes(1, 0)


def _col_letter(col_idx: int) -> str:
    """Convert a 0-based column index to an Excel column letter (A, B, ..., Z, AA, ...)."""
    result = ""
    idx = col_idx
    while True:
        result = chr(65 + idx % 26) + result
        idx = idx // 26 - 1
        if idx < 0:
            break
    return result


def _get_summary_formulas(summary: SummaryType) -> list[tuple[str, str]]:
    """Return (label, Excel function) pairs for the requested summary type."""
    if summary == SummaryType.SUM:
        return [("SUM", "SUM")]
    elif summary == SummaryType.AVG:
        return [("AVG", "AVERAGE")]
    elif summary == SummaryType.ALL:
        return [
            ("SUM", "SUM"),
            ("AVG", "AVERAGE"),
            ("MIN", "MIN"),
            ("MAX", "MAX"),
        ]
    return []


def _build_summary_fmt(
    workbook: xlsxwriter.Workbook, theme: ExcelTheme,
) -> xlsxwriter.format.Format:
    """Build the header-styled format used for summary rows."""
    border = _BORDER_MAP.get(theme.border_style, 1)
    return workbook.add_format({
        "bg_color": theme.colors.header_bg,
        "font_color": theme.colors.header_text,
        "font_name": theme.fonts.header,
        "font_size": theme.fonts.body_size,
        "bold": True,
        "border": border,
        "border_color": theme.colors.border,
        "valign": "vcenter",
    })


def _write_summary_rows(
    workbook: xlsxwriter.Workbook,
    worksheet: xlsxwriter.worksheet.Worksheet,
    sheet_data: SheetData,
    theme: ExcelTheme,
    formats: dict,
    summary: SummaryType,
) -> None:
    """Write summary formula rows at the bottom of the data."""
    formulas = _get_summary_formulas(summary)
    if not formulas:
        return

    num_rows = len(sheet_data.rows)
    summary_fmt = _build_summary_fmt(workbook, theme)
    border = _BORDER_MAP.get(theme.border_style, 1)
    first_data_row = 2  # Excel 1-indexed, row 1 is header
    last_data_row = num_rows + 1
    current_row = num_rows + 1  # 0-indexed, after all data rows

    for label, func in formulas:
        _write_summary_row(
            workbook, worksheet, sheet_data, theme,
            summary_fmt, border, current_row,
            label, func, first_data_row, last_data_row,
        )
        current_row += 1


def _write_summary_row(
    workbook: xlsxwriter.Workbook,
    worksheet: xlsxwriter.worksheet.Worksheet,
    sheet_data: SheetData,
    theme: ExcelTheme,
    summary_fmt: xlsxwriter.format.Format,
    border: int,
    row: int,
    label: str,
    func: str,
    first_data_row: int,
    last_data_row: int,
) -> None:
    """Write a single summary formula row across all columns."""
    for col_idx, col in enumerate(sheet_data.columns):
        col_ltr = _col_letter(col_idx)
        if col_idx == 0:
            worksheet.write_string(row, col_idx, label, summary_fmt)
        elif col.col_type in _NUMERIC_TYPES:
            fmt_props = {
                "bg_color": theme.colors.header_bg,
                "font_color": theme.colors.header_text,
                "font_name": theme.fonts.header,
                "font_size": theme.fonts.body_size,
                "bold": True,
                "border": border,
                "border_color": theme.colors.border,
                "valign": "vcenter",
            }
            if col.number_format:
                fmt_props["num_format"] = col.number_format
            typed_fmt = workbook.add_format(fmt_props)
            formula = f"={func}({col_ltr}{first_data_row}:{col_ltr}{last_data_row})"
            worksheet.write_formula(row, col_idx, formula, typed_fmt)
        else:
            worksheet.write_blank(row, col_idx, None, summary_fmt)


def _apply_highlight(
    workbook: xlsxwriter.Workbook,
    worksheet: xlsxwriter.worksheet.Worksheet,
    sheet_data: SheetData,
    num_rows: int,
    highlight: HighlightType,
) -> None:
    """Apply conditional formatting highlights to numeric columns."""
    for col_idx, col in enumerate(sheet_data.columns):
        if col.col_type not in _NUMERIC_TYPES:
            continue

        col_ltr = _col_letter(col_idx)
        cell_range = f"{col_ltr}2:{col_ltr}{num_rows + 1}"

        if highlight == HighlightType.BEST_WORST:
            # Green for minimum (best cost), red for maximum (worst cost)
            best_fmt = workbook.add_format({"bg_color": "#C6EFCE", "font_color": "#006100"})
            worst_fmt = workbook.add_format({"bg_color": "#FFC7CE", "font_color": "#9C0006"})

            worksheet.conditional_format(cell_range, {
                "type": "cell",
                "criteria": "==",
                "value": f"=MIN({cell_range})",
                "format": best_fmt,
            })
            worksheet.conditional_format(cell_range, {
                "type": "cell",
                "criteria": "==",
                "value": f"=MAX({cell_range})",
                "format": worst_fmt,
            })

        elif highlight == HighlightType.SCALE:
            worksheet.conditional_format(cell_range, {
                "type": "2_color_scale",
                "min_color": "#63BE7B",
                "max_color": "#F8696B",
            })
