"""Chart builder for cc-excel.

Creates chart worksheets from data using XlsxWriter's chart API.
"""

from typing import Optional

import xlsxwriter

try:
    from .models import ChartSpec, ChartType, SheetData
    from .themes import ExcelTheme
except ImportError:
    from src.models import ChartSpec, ChartType, SheetData
    from src.themes import ExcelTheme


# XlsxWriter chart type mapping
_CHART_TYPE_MAP = {
    ChartType.BAR: "bar",
    ChartType.LINE: "line",
    ChartType.PIE: "pie",
    ChartType.COLUMN: "column",
}


def add_chart(
    workbook: xlsxwriter.Workbook,
    worksheet: xlsxwriter.worksheet.Worksheet,
    chart_spec: ChartSpec,
    sheet_data: SheetData,
    theme: ExcelTheme,
) -> None:
    """Add a chart to the workbook based on the chart specification.

    Args:
        workbook: The XlsxWriter Workbook.
        worksheet: The data worksheet to reference.
        chart_spec: Chart configuration.
        sheet_data: The source data for the chart.
        theme: Theme for chart colors.
    """
    if not sheet_data.rows:
        return

    chart_type_str = _CHART_TYPE_MAP.get(chart_spec.chart_type, "column")
    chart = workbook.add_chart({"type": chart_type_str})

    data_sheet_name = sheet_data.title[:31]
    num_rows = len(sheet_data.rows)
    num_cols = len(sheet_data.columns)
    chart_colors = theme.colors.chart_colors

    # Validate column indices
    cat_col = chart_spec.category_column
    if cat_col < 0 or cat_col >= num_cols:
        raise ValueError(
            f"Chart category column index {cat_col} out of range (0-{num_cols - 1})."
        )

    for val_col in chart_spec.value_columns:
        if val_col < 0 or val_col >= num_cols:
            raise ValueError(
                f"Chart value column index {val_col} out of range (0-{num_cols - 1})."
            )

    # Add data series
    for i, val_col in enumerate(chart_spec.value_columns):
        series_config = {
            "name": [data_sheet_name, 0, val_col],
            "categories": [data_sheet_name, 1, cat_col, num_rows, cat_col],
            "values": [data_sheet_name, 1, val_col, num_rows, val_col],
        }

        # Apply theme colors to series
        color_idx = i % len(chart_colors)
        series_config["fill"] = {"color": chart_colors[color_idx]}
        series_config["border"] = {"color": chart_colors[color_idx]}

        if chart_type_str == "line":
            series_config["line"] = {"color": chart_colors[color_idx], "width": 2.25}

        chart.add_series(series_config)

    # Chart title
    chart.set_title({"name": chart_spec.title})

    # Category axis label
    if chart_type_str != "pie":
        cat_name = sheet_data.columns[cat_col].name
        chart.set_x_axis({"name": cat_name})

        if len(chart_spec.value_columns) == 1:
            val_name = sheet_data.columns[chart_spec.value_columns[0]].name
            chart.set_y_axis({"name": val_name})

    # Chart size
    chart.set_size({"width": 720, "height": 480})

    # Add chart to a new worksheet
    chart_sheet_name = chart_spec.sheet_name[:31]
    chart_worksheet = workbook.add_chartsheet(chart_sheet_name)
    chart_worksheet.set_chart(chart)
