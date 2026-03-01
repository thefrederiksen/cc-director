"""Tests for chart builder."""

import pytest
from pathlib import Path
from src.models import SheetData, ColumnInfo, ColumnType, ChartSpec, ChartType
from src.xlsx_generator import generate_xlsx
from src.themes import get_theme


def _make_chart_sheet():
    """Create a sheet suitable for charting."""
    columns = [
        ColumnInfo(name="Quarter", col_type=ColumnType.TEXT, width=12),
        ColumnInfo(name="Revenue", col_type=ColumnType.INTEGER, width=12, number_format="#,##0"),
        ColumnInfo(name="Profit", col_type=ColumnType.INTEGER, width=12, number_format="#,##0"),
    ]
    rows = [
        ["Q1", 100000, 20000],
        ["Q2", 120000, 25000],
        ["Q3", 115000, 22000],
        ["Q4", 140000, 30000],
    ]
    return SheetData(title="Sales", columns=columns, rows=rows)


class TestChartBuilder:
    def test_bar_chart(self, tmp_dir):
        sheet = _make_chart_sheet()
        spec = ChartSpec(
            chart_type=ChartType.BAR,
            title="Revenue by Quarter",
            category_column=0,
            value_columns=[1],
        )
        output = tmp_dir / "bar_chart.xlsx"
        generate_xlsx([sheet], get_theme("paper"), output, chart_spec=spec)
        assert output.exists()
        assert output.stat().st_size > 0

    def test_line_chart(self, tmp_dir):
        sheet = _make_chart_sheet()
        spec = ChartSpec(
            chart_type=ChartType.LINE,
            title="Trend",
            category_column=0,
            value_columns=[1, 2],
        )
        output = tmp_dir / "line_chart.xlsx"
        generate_xlsx([sheet], get_theme("boardroom"), output, chart_spec=spec)
        assert output.exists()

    def test_pie_chart(self, tmp_dir):
        sheet = _make_chart_sheet()
        spec = ChartSpec(
            chart_type=ChartType.PIE,
            title="Revenue Distribution",
            category_column=0,
            value_columns=[1],
        )
        output = tmp_dir / "pie_chart.xlsx"
        generate_xlsx([sheet], get_theme("spark"), output, chart_spec=spec)
        assert output.exists()

    def test_column_chart(self, tmp_dir):
        sheet = _make_chart_sheet()
        spec = ChartSpec(
            chart_type=ChartType.COLUMN,
            title="Comparison",
            category_column=0,
            value_columns=[1, 2],
        )
        output = tmp_dir / "column_chart.xlsx"
        generate_xlsx([sheet], get_theme("terminal"), output, chart_spec=spec)
        assert output.exists()

    def test_invalid_category_column(self, tmp_dir):
        sheet = _make_chart_sheet()
        spec = ChartSpec(
            chart_type=ChartType.BAR,
            title="Bad",
            category_column=99,
            value_columns=[1],
        )
        output = tmp_dir / "bad_chart.xlsx"
        with pytest.raises(ValueError, match="out of range"):
            generate_xlsx([sheet], get_theme("paper"), output, chart_spec=spec)

    def test_invalid_value_column(self, tmp_dir):
        sheet = _make_chart_sheet()
        spec = ChartSpec(
            chart_type=ChartType.BAR,
            title="Bad",
            category_column=0,
            value_columns=[99],
        )
        output = tmp_dir / "bad_chart2.xlsx"
        with pytest.raises(ValueError, match="out of range"):
            generate_xlsx([sheet], get_theme("paper"), output, chart_spec=spec)
