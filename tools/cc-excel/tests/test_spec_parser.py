"""Tests for spec_parser -- JSON workbook specification parsing."""

import json
import pytest
from pathlib import Path
from src.spec_parser import parse_spec
from src.spec_models import (
    CellSpec,
    ConditionalFormatSpec,
    RowSpec,
    SheetSpec,
    StyleType,
    WorkbookSpec,
)


def _write_spec(tmp_path: Path, data: dict) -> Path:
    """Helper to write a spec dict to a temp JSON file."""
    path = tmp_path / "spec.json"
    path.write_text(json.dumps(data, indent=2), encoding="utf-8")
    return path


class TestParseSpecBasic:
    def test_minimal_spec(self, tmp_path):
        spec_data = {
            "sheets": [
                {
                    "name": "Sheet1",
                    "rows": [
                        {"cells": ["Hello", 42, 3.14]},
                    ],
                }
            ]
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)

        assert isinstance(spec, WorkbookSpec)
        assert len(spec.sheets) == 1
        assert spec.sheets[0].name == "Sheet1"
        assert spec.theme is None
        assert spec.named_ranges == {}

    def test_spec_with_theme(self, tmp_path):
        spec_data = {
            "theme": "boardroom",
            "sheets": [{"name": "S1", "rows": []}],
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        assert spec.theme == "boardroom"

    def test_spec_with_named_ranges(self, tmp_path):
        spec_data = {
            "named_ranges": {
                "annual_km": "INPUT!$B$5",
                "gas_price": "INPUT!$B$7",
            },
            "sheets": [{"name": "INPUT", "rows": []}],
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        assert spec.named_ranges == {
            "annual_km": "INPUT!$B$5",
            "gas_price": "INPUT!$B$7",
        }

    def test_file_not_found(self, tmp_path):
        with pytest.raises(FileNotFoundError):
            parse_spec(tmp_path / "nonexistent.json")

    def test_invalid_json(self, tmp_path):
        path = tmp_path / "bad.json"
        path.write_text("{ not valid json }", encoding="utf-8")
        with pytest.raises(ValueError, match="Invalid JSON"):
            parse_spec(path)

    def test_missing_sheets(self, tmp_path):
        path = _write_spec(tmp_path, {"theme": "paper"})
        with pytest.raises(ValueError, match="sheets"):
            parse_spec(path)

    def test_empty_sheets(self, tmp_path):
        path = _write_spec(tmp_path, {"sheets": []})
        with pytest.raises(ValueError, match="at least one sheet"):
            parse_spec(path)

    def test_top_level_not_object(self, tmp_path):
        path = tmp_path / "list.json"
        path.write_text("[]", encoding="utf-8")
        with pytest.raises(ValueError, match="JSON object"):
            parse_spec(path)


class TestParseSheets:
    def test_sheet_with_columns(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "Data",
                "columns": [30, 15, 20],
                "rows": [],
            }]
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        assert spec.sheets[0].columns == [30, 15, 20]

    def test_sheet_with_freeze(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "Data",
                "freeze": [1, 0],
                "rows": [],
            }]
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        assert spec.sheets[0].freeze == [1, 0]

    def test_sheet_missing_name(self, tmp_path):
        path = _write_spec(tmp_path, {"sheets": [{"rows": []}]})
        with pytest.raises(ValueError, match="name"):
            parse_spec(path)

    def test_sheet_autofilter_true(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "Data",
                "autofilter": True,
                "rows": [],
            }]
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        assert spec.sheets[0].autofilter is True

    def test_sheet_autofilter_list(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "Data",
                "autofilter": [0, 0, 10, 3],
                "rows": [],
            }]
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        assert spec.sheets[0].autofilter == [0, 0, 10, 3]

    def test_multiple_sheets(self, tmp_path):
        spec_data = {
            "sheets": [
                {"name": "Sheet1", "rows": []},
                {"name": "Sheet2", "rows": []},
                {"name": "Sheet3", "rows": []},
            ]
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        assert len(spec.sheets) == 3
        assert [s.name for s in spec.sheets] == ["Sheet1", "Sheet2", "Sheet3"]


class TestParseRows:
    def test_null_row_spacer(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [
                    {"cells": ["A"]},
                    None,
                    {"cells": ["B"]},
                ],
            }]
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        rows = spec.sheets[0].rows
        assert len(rows) == 3
        assert rows[0] is not None
        assert rows[1] is None
        assert rows[2] is not None

    def test_merged_title_row(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [
                    {"merge": 3, "value": "TITLE", "style": "title"},
                ],
            }]
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        row = spec.sheets[0].rows[0]
        assert row.merge == 3
        assert row.value == "TITLE"
        assert row.style == StyleType.TITLE

    def test_row_with_style(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [
                    {"style": "header", "cells": ["Col1", "Col2"]},
                ],
            }]
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        row = spec.sheets[0].rows[0]
        assert row.style == StyleType.HEADER
        assert len(row.cells) == 2

    def test_row_with_height(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [
                    {"cells": ["A"], "height": 30.0},
                ],
            }]
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        assert spec.sheets[0].rows[0].height == 30.0

    def test_invalid_style(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [{"style": "invalid_style", "cells": ["A"]}],
            }]
        }
        path = _write_spec(tmp_path, spec_data)
        with pytest.raises(ValueError, match="unknown style"):
            parse_spec(path)


class TestParseCells:
    def test_literal_values(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [
                    {"cells": ["text", 42, 3.14, True, None]},
                ],
            }]
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        cells = spec.sheets[0].rows[0].cells
        assert cells[0] == "text"
        assert cells[1] == 42
        assert cells[2] == 3.14
        assert cells[3] is True
        assert cells[4] is None

    def test_cell_object_with_value(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [
                    {"cells": [{"v": 100, "fmt": "#,##0", "style": "input"}]},
                ],
            }]
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        cell = spec.sheets[0].rows[0].cells[0]
        assert isinstance(cell, CellSpec)
        assert cell.value == 100
        assert cell.number_format == "#,##0"
        assert cell.style == StyleType.INPUT

    def test_cell_object_with_formula(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [
                    {"cells": [{"f": "=SUM(B2:B10)"}]},
                ],
            }]
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        cell = spec.sheets[0].rows[0].cells[0]
        assert isinstance(cell, CellSpec)
        assert cell.formula == "=SUM(B2:B10)"

    def test_cell_object_with_merge(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [
                    {"cells": [{"v": "Merged", "merge": 3}]},
                ],
            }]
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        cell = spec.sheets[0].rows[0].cells[0]
        assert isinstance(cell, CellSpec)
        assert cell.merge == 3

    def test_cell_object_with_comment(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [
                    {"cells": [{"v": 42, "comment": "This is a note"}]},
                ],
            }]
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        cell = spec.sheets[0].rows[0].cells[0]
        assert isinstance(cell, CellSpec)
        assert cell.comment == "This is a note"


class TestParseConditionalFormats:
    def test_color_scale(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [],
                "conditional_formats": [{
                    "range": "B2:D10",
                    "type": "color_scale",
                    "min_color": "#63BE7B",
                    "max_color": "#F8696B",
                }],
            }]
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        cf = spec.sheets[0].conditional_formats[0]
        assert isinstance(cf, ConditionalFormatSpec)
        assert cf.range == "B2:D10"
        assert cf.type == "color_scale"
        assert cf.min_color == "#63BE7B"
        assert cf.max_color == "#F8696B"

    def test_cell_criteria(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [],
                "conditional_formats": [{
                    "range": "B2:B10",
                    "type": "cell",
                    "criteria": ">=",
                    "value": 100,
                    "bg_color": "#C6EFCE",
                }],
            }]
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        cf = spec.sheets[0].conditional_formats[0]
        assert cf.criteria == ">="
        assert cf.value == 100
        assert cf.bg_color == "#C6EFCE"

    def test_three_color_scale(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [],
                "conditional_formats": [{
                    "range": "A1:A10",
                    "type": "3_color_scale",
                    "min_color": "#F8696B",
                    "mid_color": "#FFEB84",
                    "max_color": "#63BE7B",
                }],
            }]
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        cf = spec.sheets[0].conditional_formats[0]
        assert cf.type == "3_color_scale"
        assert cf.mid_color == "#FFEB84"

    def test_missing_range(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [],
                "conditional_formats": [{"type": "color_scale"}],
            }]
        }
        path = _write_spec(tmp_path, spec_data)
        with pytest.raises(ValueError, match="range"):
            parse_spec(path)


class TestParseDataValidation:
    def test_list_validation(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [],
                "data_validation": [{
                    "range": "A2:A10",
                    "type": "list",
                    "source": ["Yes", "No", "Maybe"],
                }],
            }]
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        dv = spec.sheets[0].data_validations[0]
        assert dv.type == "list"
        assert dv.source == ["Yes", "No", "Maybe"]

    def test_integer_validation(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [],
                "data_validation": [{
                    "range": "B2:B10",
                    "type": "integer",
                    "criteria": "between",
                    "minimum": 1,
                    "maximum": 100,
                    "error_title": "Invalid",
                    "error_message": "Enter 1-100",
                }],
            }]
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)
        dv = spec.sheets[0].data_validations[0]
        assert dv.type == "integer"
        assert dv.minimum == 1
        assert dv.maximum == 100
        assert dv.error_title == "Invalid"


class TestComplexSpec:
    def test_multi_sheet_with_formulas(self, tmp_path):
        """Test a realistic multi-sheet spec with formulas and styles."""
        spec_data = {
            "theme": "boardroom",
            "named_ranges": {"annual_km": "INPUT!$B$3"},
            "sheets": [
                {
                    "name": "INPUT",
                    "columns": [30, 15],
                    "freeze": [2, 0],
                    "rows": [
                        {"merge": 2, "value": "Input Variables", "style": "title"},
                        {"style": "header", "cells": ["Parameter", "Value"]},
                        {"cells": ["Annual Driving", {"v": 20000, "fmt": "#,##0", "style": "input"}]},
                    ],
                },
                {
                    "name": "COSTS",
                    "columns": [20, 15, 15],
                    "freeze": [1, 1],
                    "rows": [
                        {"style": "header", "cells": ["Item", "Car A", "Car B"]},
                        {"cells": ["Price", {"f": "=INPUT!B3*0.5"}, {"f": "=INPUT!B3*0.3"}]},
                        {"style": "total", "cells": ["TOTAL", {"f": "=SUM(B2:B2)"}, {"f": "=SUM(C2:C2)"}]},
                    ],
                    "conditional_formats": [
                        {"range": "B3:C3", "type": "color_scale", "min_color": "#63BE7B", "max_color": "#F8696B"},
                    ],
                },
            ],
        }
        path = _write_spec(tmp_path, spec_data)
        spec = parse_spec(path)

        assert spec.theme == "boardroom"
        assert len(spec.sheets) == 2
        assert spec.named_ranges == {"annual_km": "INPUT!$B$3"}

        # INPUT sheet
        input_sheet = spec.sheets[0]
        assert input_sheet.name == "INPUT"
        assert input_sheet.columns == [30, 15]
        assert input_sheet.freeze == [2, 0]
        assert len(input_sheet.rows) == 3

        # Title row
        title_row = input_sheet.rows[0]
        assert title_row.merge == 2
        assert title_row.value == "Input Variables"
        assert title_row.style == StyleType.TITLE

        # Input cell
        input_cell = input_sheet.rows[2].cells[1]
        assert isinstance(input_cell, CellSpec)
        assert input_cell.value == 20000
        assert input_cell.style == StyleType.INPUT

        # COSTS sheet
        costs_sheet = spec.sheets[1]
        assert costs_sheet.name == "COSTS"
        assert len(costs_sheet.conditional_formats) == 1

        # Formula cell
        formula_cell = costs_sheet.rows[1].cells[1]
        assert isinstance(formula_cell, CellSpec)
        assert formula_cell.formula == "=INPUT!B3*0.5"
