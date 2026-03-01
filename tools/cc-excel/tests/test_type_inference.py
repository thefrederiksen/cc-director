"""Tests for type inference engine."""

import pytest
from datetime import datetime
from src.models import SheetData, ColumnInfo, ColumnType
from src.type_inference import infer_types


def _make_sheet(headers, rows):
    """Helper to build a SheetData from headers and rows."""
    columns = [ColumnInfo(name=h) for h in headers]
    return SheetData(title="test", columns=columns, rows=rows)


class TestInferTypes:
    def test_integer_detection(self):
        sheet = _make_sheet(["count"], [["100"], ["200"], ["300"]])
        infer_types(sheet)
        assert sheet.columns[0].col_type == ColumnType.INTEGER
        assert sheet.columns[0].number_format == "#,##0"
        assert sheet.rows[0][0] == 100

    def test_float_detection(self):
        sheet = _make_sheet(["price"], [["19.99"], ["29.50"], ["9.95"]])
        infer_types(sheet)
        assert sheet.columns[0].col_type == ColumnType.FLOAT
        assert sheet.columns[0].number_format == "#,##0.00"
        assert sheet.rows[0][0] == 19.99

    def test_percentage_detection(self):
        sheet = _make_sheet(["growth"], [["12.5%"], ["8.3%"], ["15.7%"]])
        infer_types(sheet)
        assert sheet.columns[0].col_type == ColumnType.PERCENTAGE
        assert sheet.columns[0].number_format == "0.0%"
        assert abs(sheet.rows[0][0] - 0.125) < 0.001

    def test_currency_detection(self):
        sheet = _make_sheet(["price"], [["$19.99"], ["$49.99"], ["$9.99"]])
        infer_types(sheet)
        assert sheet.columns[0].col_type == ColumnType.CURRENCY
        assert sheet.columns[0].number_format == "$#,##0.00"
        assert sheet.rows[0][0] == 19.99

    def test_date_detection(self):
        sheet = _make_sheet(["date"], [["2025-01-15"], ["2025-02-20"], ["2025-03-10"]])
        infer_types(sheet)
        assert sheet.columns[0].col_type == ColumnType.DATE
        assert isinstance(sheet.rows[0][0], datetime)

    def test_boolean_detection(self):
        sheet = _make_sheet(["active"], [["true"], ["false"], ["true"]])
        infer_types(sheet)
        assert sheet.columns[0].col_type == ColumnType.BOOLEAN
        assert sheet.rows[0][0] is True
        assert sheet.rows[1][0] is False

    def test_text_fallback(self):
        sheet = _make_sheet(["name"], [["Alice"], ["Bob"], ["Charlie"]])
        infer_types(sheet)
        assert sheet.columns[0].col_type == ColumnType.TEXT

    def test_mixed_column_majority_wins(self):
        sheet = _make_sheet(["val"], [["100"], ["200"], ["abc"], ["300"]])
        infer_types(sheet)
        # 3 out of 4 are integers -> INTEGER
        assert sheet.columns[0].col_type == ColumnType.INTEGER

    def test_empty_column_stays_text(self):
        sheet = _make_sheet(["empty"], [[""], [""], [""]])
        infer_types(sheet)
        assert sheet.columns[0].col_type == ColumnType.TEXT

    def test_width_calculation(self):
        sheet = _make_sheet(["short"], [["a"], ["b"]])
        infer_types(sheet)
        assert sheet.columns[0].width >= 8.0  # minimum width

    def test_comma_separated_integers(self):
        sheet = _make_sheet(["amount"], [["1,000"], ["2,500"], ["10,000"]])
        infer_types(sheet)
        assert sheet.columns[0].col_type == ColumnType.INTEGER
        assert sheet.rows[0][0] == 1000

    def test_empty_sheet(self):
        sheet = _make_sheet([], [])
        result = infer_types(sheet)
        assert result is sheet  # returns same object

    def test_multiple_columns(self):
        sheet = _make_sheet(
            ["name", "score", "pct"],
            [["Alice", "95", "90%"], ["Bob", "87", "80%"]],
        )
        infer_types(sheet)
        assert sheet.columns[0].col_type == ColumnType.TEXT
        assert sheet.columns[1].col_type == ColumnType.INTEGER
        assert sheet.columns[2].col_type == ColumnType.PERCENTAGE
