"""Tests for cc-excel markdown converter (XLSX -> Markdown)."""

from pathlib import Path

import openpyxl
import pytest

from src.md_converter import convert_xlsx_to_markdown


def _create_workbook(path: Path, sheets: dict[str, list[list]]) -> Path:
    """Helper: create a test workbook with given sheet data."""
    wb = openpyxl.Workbook()
    first = True
    for name, rows in sheets.items():
        if first:
            ws = wb.active
            ws.title = name
            first = False
        else:
            ws = wb.create_sheet(name)
        for row in rows:
            ws.append(row)
    wb.save(str(path))
    return path


class TestConvertXlsxToMarkdown:
    """Tests for convert_xlsx_to_markdown."""

    def test_basic_table(self, tmp_path):
        xlsx = _create_workbook(
            tmp_path / "test.xlsx",
            {"Data": [["Name", "Age"], ["Alice", 30], ["Bob", 25]]},
        )

        md = convert_xlsx_to_markdown(xlsx)

        assert "Name" in md
        assert "Alice" in md
        assert "Bob" in md
        assert "|" in md  # pipe table format

    def test_first_sheet_only_by_default(self, tmp_path):
        xlsx = _create_workbook(
            tmp_path / "test.xlsx",
            {
                "Sheet1": [["Col1"], ["val1"]],
                "Sheet2": [["Col2"], ["val2"]],
            },
        )

        md = convert_xlsx_to_markdown(xlsx)

        assert "Sheet1" in md
        assert "Sheet2" not in md

    def test_all_sheets(self, tmp_path):
        xlsx = _create_workbook(
            tmp_path / "test.xlsx",
            {
                "Sheet1": [["Col1"], ["val1"]],
                "Sheet2": [["Col2"], ["val2"]],
            },
        )

        md = convert_xlsx_to_markdown(xlsx, all_sheets=True)

        assert "## Sheet1" in md
        assert "## Sheet2" in md
        assert "val1" in md
        assert "val2" in md

    def test_specific_sheet_by_name(self, tmp_path):
        xlsx = _create_workbook(
            tmp_path / "test.xlsx",
            {
                "Alpha": [["A"], ["1"]],
                "Beta": [["B"], ["2"]],
            },
        )

        md = convert_xlsx_to_markdown(xlsx, sheet_name="Beta")

        assert "## Beta" in md
        assert "## Alpha" not in md

    def test_invalid_sheet_name_raises(self, tmp_path):
        xlsx = _create_workbook(
            tmp_path / "test.xlsx",
            {"Only": [["X"], ["Y"]]},
        )

        with pytest.raises(ValueError, match="not found"):
            convert_xlsx_to_markdown(xlsx, sheet_name="Missing")

    def test_empty_sheet(self, tmp_path):
        xlsx = _create_workbook(tmp_path / "test.xlsx", {"Empty": []})

        md = convert_xlsx_to_markdown(xlsx)

        assert "Empty" in md
        assert "Empty sheet" in md

    def test_pipe_in_cell_is_escaped(self, tmp_path):
        xlsx = _create_workbook(
            tmp_path / "test.xlsx",
            {"Data": [["Value"], ["A|B"]]},
        )

        md = convert_xlsx_to_markdown(xlsx)

        assert "A\\|B" in md

    def test_output_ends_with_newline(self, tmp_path):
        xlsx = _create_workbook(
            tmp_path / "test.xlsx",
            {"Data": [["X"], ["Y"]]},
        )

        md = convert_xlsx_to_markdown(xlsx)

        assert md.endswith("\n")

    def test_separator_row_present(self, tmp_path):
        xlsx = _create_workbook(
            tmp_path / "test.xlsx",
            {"Data": [["A", "B"], ["1", "2"]]},
        )

        md = convert_xlsx_to_markdown(xlsx)

        lines = md.strip().split("\n")
        # Sheet heading, blank line, header, separator, data
        sep_line = [l for l in lines if set(l.replace(" ", "").replace("|", "")) == {"-"}]
        assert len(sep_line) >= 1
