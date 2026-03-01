"""Tests for XLSX generator."""

import pytest
from pathlib import Path
from src.models import SheetData, ColumnInfo, ColumnType
from src.xlsx_generator import generate_xlsx
from src.themes import get_theme, _THEMES
from src.type_inference import infer_types


def _make_sheet():
    """Create a simple sheet for testing."""
    columns = [
        ColumnInfo(name="Name", col_type=ColumnType.TEXT, width=15),
        ColumnInfo(name="Score", col_type=ColumnType.INTEGER, width=10, number_format="#,##0"),
        ColumnInfo(name="Grade", col_type=ColumnType.TEXT, width=10),
    ]
    rows = [
        ["Alice", 95, "A"],
        ["Bob", 87, "B+"],
        ["Charlie", 92, "A-"],
    ]
    return SheetData(title="Results", columns=columns, rows=rows)


class TestGenerateXLSX:
    def test_creates_file(self, tmp_dir):
        sheet = _make_sheet()
        output = tmp_dir / "test.xlsx"
        generate_xlsx([sheet], get_theme("paper"), output)
        assert output.exists()
        assert output.stat().st_size > 0

    def test_all_themes_produce_valid_output(self, tmp_dir):
        sheet = _make_sheet()
        for theme_name in _THEMES:
            output = tmp_dir / f"test_{theme_name}.xlsx"
            generate_xlsx([sheet], get_theme(theme_name), output)
            assert output.exists(), f"Theme '{theme_name}' failed to produce output"
            assert output.stat().st_size > 0, f"Theme '{theme_name}' produced empty file"

    def test_autofilter_disabled(self, tmp_dir):
        sheet = _make_sheet()
        output = tmp_dir / "no_filter.xlsx"
        generate_xlsx([sheet], get_theme("paper"), output, autofilter=False)
        assert output.exists()

    def test_freeze_disabled(self, tmp_dir):
        sheet = _make_sheet()
        output = tmp_dir / "no_freeze.xlsx"
        generate_xlsx([sheet], get_theme("paper"), output, freeze=False)
        assert output.exists()

    def test_multiple_sheets(self, tmp_dir):
        sheet1 = _make_sheet()
        sheet2 = SheetData(
            title="Sheet2",
            columns=[ColumnInfo(name="Col1", width=10)],
            rows=[["val1"], ["val2"]],
        )
        output = tmp_dir / "multi.xlsx"
        generate_xlsx([sheet1, sheet2], get_theme("paper"), output)
        assert output.exists()

    def test_empty_sheet(self, tmp_dir):
        sheet = SheetData(title="Empty", columns=[], rows=[])
        output = tmp_dir / "empty.xlsx"
        generate_xlsx([sheet], get_theme("paper"), output)
        assert output.exists()

    def test_with_type_inference(self, tmp_dir):
        columns = [ColumnInfo(name="Val")]
        rows = [["100"], ["200"], ["300"]]
        sheet = SheetData(title="Typed", columns=columns, rows=rows)
        infer_types(sheet)
        output = tmp_dir / "typed.xlsx"
        generate_xlsx([sheet], get_theme("boardroom"), output)
        assert output.exists()
