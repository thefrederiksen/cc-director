"""Tests for spec_generator -- XLSX generation from workbook specs."""

import json
import pytest
from pathlib import Path
from src.spec_parser import parse_spec
from src.spec_generator import generate_from_spec
from src.spec_models import (
    CellSpec,
    ConditionalFormatSpec,
    RowSpec,
    SheetSpec,
    StyleType,
    WorkbookSpec,
)
from src.themes import get_theme, _THEMES


def _write_spec(tmp_path: Path, data: dict) -> Path:
    """Helper to write a spec dict to a temp JSON file."""
    path = tmp_path / "spec.json"
    path.write_text(json.dumps(data, indent=2), encoding="utf-8")
    return path


def _generate(tmp_path: Path, spec_data: dict, theme_name: str = "paper") -> Path:
    """Helper: write spec, parse, generate, return output path."""
    path = _write_spec(tmp_path, spec_data)
    spec = parse_spec(path)
    output = tmp_path / "output.xlsx"
    theme = get_theme(theme_name)
    generate_from_spec(spec, theme, output)
    return output


class TestBasicGeneration:
    def test_creates_file(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "Sheet1",
                "rows": [
                    {"cells": ["Hello", 42, 3.14]},
                ],
            }]
        }
        output = _generate(tmp_path, spec_data)
        assert output.exists()
        assert output.stat().st_size > 0

    def test_empty_sheet(self, tmp_path):
        spec_data = {"sheets": [{"name": "Empty", "rows": []}]}
        output = _generate(tmp_path, spec_data)
        assert output.exists()

    def test_all_themes(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "Test",
                "rows": [
                    {"style": "header", "cells": ["Name", "Value"]},
                    {"cells": ["Alpha", 100]},
                ],
            }]
        }
        for theme_name in _THEMES:
            path = _write_spec(tmp_path, spec_data)
            spec = parse_spec(path)
            output = tmp_path / f"test_{theme_name}.xlsx"
            generate_from_spec(spec, get_theme(theme_name), output)
            assert output.exists(), f"Theme '{theme_name}' failed"
            assert output.stat().st_size > 0


class TestStyles:
    def test_header_style(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [
                    {"style": "header", "cells": ["Col1", "Col2", "Col3"]},
                    {"cells": ["A", "B", "C"]},
                ],
            }]
        }
        output = _generate(tmp_path, spec_data)
        assert output.exists()

    def test_all_row_styles(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "Styles",
                "rows": [
                    {"style": "header", "cells": ["Header"]},
                    {"style": "subheader", "cells": ["Subheader"]},
                    {"style": "total", "cells": ["Total"]},
                    {"cells": ["Normal"]},
                ],
            }]
        }
        output = _generate(tmp_path, spec_data)
        assert output.exists()

    def test_cell_level_styles(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "CellStyles",
                "rows": [
                    {"cells": [
                        {"v": "Input", "style": "input"},
                        {"v": "Best", "style": "best"},
                        {"v": "Worst", "style": "worst"},
                        {"v": "Accent", "style": "accent"},
                    ]},
                ],
            }]
        }
        output = _generate(tmp_path, spec_data)
        assert output.exists()


class TestMergedCells:
    def test_merged_title_row(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [
                    {"merge": 3, "value": "Report Title", "style": "title"},
                    {"merge": 3, "value": "Subtitle text", "style": "subtitle"},
                    {"style": "header", "cells": ["A", "B", "C"]},
                ],
            }]
        }
        output = _generate(tmp_path, spec_data)
        assert output.exists()

    def test_cell_level_merge(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [
                    {"cells": [{"v": "Wide cell", "merge": 2}, "Normal"]},
                ],
            }]
        }
        output = _generate(tmp_path, spec_data)
        assert output.exists()


class TestFormulas:
    def test_simple_formula(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "Calc",
                "rows": [
                    {"style": "header", "cells": ["A", "B", "Sum"]},
                    {"cells": [10, 20, {"f": "=A2+B2"}]},
                    {"cells": [30, 40, {"f": "=A3+B3"}]},
                ],
            }]
        }
        output = _generate(tmp_path, spec_data)
        assert output.exists()

    def test_sum_formula(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "Totals",
                "rows": [
                    {"style": "header", "cells": ["Item", "Amount"]},
                    {"cells": ["A", 100]},
                    {"cells": ["B", 200]},
                    {"cells": ["C", 300]},
                    {"style": "total", "cells": ["Total", {"f": "=SUM(B2:B4)"}]},
                ],
            }]
        }
        output = _generate(tmp_path, spec_data)
        assert output.exists()

    def test_cross_sheet_formula(self, tmp_path):
        spec_data = {
            "sheets": [
                {
                    "name": "Input",
                    "rows": [
                        {"cells": ["Value", 100]},
                    ],
                },
                {
                    "name": "Output",
                    "rows": [
                        {"cells": ["Result", {"f": "=Input!B1*2"}]},
                    ],
                },
            ]
        }
        output = _generate(tmp_path, spec_data)
        assert output.exists()


class TestNumberFormats:
    def test_custom_number_format(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "Formats",
                "rows": [
                    {"cells": [
                        {"v": 1234567, "fmt": "#,##0"},
                        {"v": 0.156, "fmt": "0.0%"},
                        {"v": 49.99, "fmt": "$#,##0.00"},
                    ]},
                ],
            }]
        }
        output = _generate(tmp_path, spec_data)
        assert output.exists()


class TestColumnWidths:
    def test_custom_column_widths(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "columns": [30, 15, 20, 10],
                "rows": [
                    {"cells": ["Wide", "Medium", "Normal", "Narrow"]},
                ],
            }]
        }
        output = _generate(tmp_path, spec_data)
        assert output.exists()


class TestFreezePanes:
    def test_freeze_panes(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "freeze": [1, 0],
                "rows": [
                    {"style": "header", "cells": ["A", "B"]},
                    {"cells": [1, 2]},
                ],
            }]
        }
        output = _generate(tmp_path, spec_data)
        assert output.exists()

    def test_freeze_row_and_column(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "freeze": [1, 1],
                "rows": [
                    {"style": "header", "cells": ["A", "B", "C"]},
                    {"cells": [1, 2, 3]},
                ],
            }]
        }
        output = _generate(tmp_path, spec_data)
        assert output.exists()


class TestConditionalFormats:
    def test_color_scale(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [
                    {"style": "header", "cells": ["Val"]},
                    {"cells": [10]},
                    {"cells": [50]},
                    {"cells": [90]},
                ],
                "conditional_formats": [{
                    "range": "A2:A4",
                    "type": "color_scale",
                    "min_color": "#63BE7B",
                    "max_color": "#F8696B",
                }],
            }]
        }
        output = _generate(tmp_path, spec_data)
        assert output.exists()

    def test_cell_criteria_format(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [
                    {"style": "header", "cells": ["Score"]},
                    {"cells": [85]},
                    {"cells": [42]},
                    {"cells": [95]},
                ],
                "conditional_formats": [{
                    "range": "A2:A4",
                    "type": "cell",
                    "criteria": ">=",
                    "value": 90,
                    "bg_color": "#C6EFCE",
                }],
            }]
        }
        output = _generate(tmp_path, spec_data)
        assert output.exists()


class TestNamedRanges:
    def test_named_ranges_defined(self, tmp_path):
        spec_data = {
            "named_ranges": {
                "my_range": "Sheet1!$A$1:$A$5",
            },
            "sheets": [{
                "name": "Sheet1",
                "rows": [
                    {"cells": [1]},
                    {"cells": [2]},
                    {"cells": [3]},
                    {"cells": [4]},
                    {"cells": [5]},
                ],
            }]
        }
        output = _generate(tmp_path, spec_data)
        assert output.exists()


class TestSpacerRows:
    def test_null_rows_as_spacers(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [
                    {"cells": ["Before"]},
                    None,
                    None,
                    {"cells": ["After"]},
                ],
            }]
        }
        output = _generate(tmp_path, spec_data)
        assert output.exists()


class TestComments:
    def test_cell_comment(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [
                    {"cells": [{"v": 42, "comment": "The answer"}]},
                ],
            }]
        }
        output = _generate(tmp_path, spec_data)
        assert output.exists()


class TestRowHeight:
    def test_custom_row_height(self, tmp_path):
        spec_data = {
            "sheets": [{
                "name": "S1",
                "rows": [
                    {"cells": ["Tall row"], "height": 40.0},
                    {"cells": ["Normal row"]},
                ],
            }]
        }
        output = _generate(tmp_path, spec_data)
        assert output.exists()


class TestVehicleComparisonPattern:
    """Test a realistic multi-sheet workbook pattern similar to the vehicle comparison."""

    def test_full_workbook(self, tmp_path):
        spec_data = {
            "theme": "boardroom",
            "named_ranges": {
                "annual_km": "INPUT!$B$4",
                "gas_price": "INPUT!$B$5",
            },
            "sheets": [
                {
                    "name": "INPUT",
                    "columns": [30, 15, 20],
                    "freeze": [3, 0],
                    "rows": [
                        {"merge": 3, "value": "INPUT VARIABLES", "style": "title"},
                        {"merge": 3, "value": "Change yellow cells to update", "style": "subtitle"},
                        None,
                        {"style": "header", "cells": ["Parameter", "Value", "Unit"]},
                        {"cells": [
                            "Annual Driving",
                            {"v": 20000, "fmt": "#,##0", "style": "input"},
                            "km/year",
                        ]},
                        {"cells": [
                            "Gas Price",
                            {"v": 1.50, "fmt": "$#,##0.00", "style": "input"},
                            "$/liter",
                        ]},
                    ],
                },
                {
                    "name": "COSTS",
                    "columns": [20, 15, 15, 15],
                    "freeze": [1, 1],
                    "rows": [
                        {"style": "header", "cells": ["Cost Item", "Car A", "Car B", "Car C"]},
                        {"cells": [
                            "Monthly Fuel",
                            {"f": "=(INPUT!$B$4/12)*(8/100)*INPUT!$B$5"},
                            {"f": "=(INPUT!$B$4/12)*(6/100)*INPUT!$B$5"},
                            {"f": "=(INPUT!$B$4/12)*(4/100)*INPUT!$B$5"},
                        ]},
                        {"cells": ["Monthly Lease", 1100, 750, 875]},
                        {"style": "total", "cells": [
                            "TOTAL",
                            {"f": "=SUM(B2:B3)"},
                            {"f": "=SUM(C2:C3)"},
                            {"f": "=SUM(D2:D3)"},
                        ]},
                    ],
                    "conditional_formats": [
                        {
                            "range": "B4:D4",
                            "type": "color_scale",
                            "min_color": "#63BE7B",
                            "max_color": "#F8696B",
                        },
                    ],
                },
                {
                    "name": "SUMMARY",
                    "columns": [20, 15, 15, 15],
                    "rows": [
                        {"merge": 4, "value": "Vehicle Comparison Summary", "style": "title"},
                        None,
                        {"style": "header", "cells": ["Metric", "Car A", "Car B", "Car C"]},
                        {"cells": [
                            "Monthly Cost",
                            {"f": "=COSTS!B4", "fmt": "$#,##0.00"},
                            {"f": "=COSTS!C4", "fmt": "$#,##0.00"},
                            {"f": "=COSTS!D4", "fmt": "$#,##0.00"},
                        ]},
                        {"cells": [
                            "Best Value?",
                            {"v": "", "style": "worst"},
                            {"v": "YES", "style": "best"},
                            {"v": "", "style": "accent"},
                        ]},
                    ],
                },
            ],
        }
        output = _generate(tmp_path, spec_data, "boardroom")
        assert output.exists()
        assert output.stat().st_size > 0


class TestSummaryAndHighlight:
    """Test the --summary and --highlight features on the standard xlsx_generator."""

    def test_summary_sum(self, tmp_path):
        from src.models import SheetData, ColumnInfo, ColumnType, SummaryType
        from src.xlsx_generator import generate_xlsx

        columns = [
            ColumnInfo(name="Name", col_type=ColumnType.TEXT, width=15),
            ColumnInfo(name="Revenue", col_type=ColumnType.INTEGER, width=12, number_format="#,##0"),
        ]
        rows = [["Alpha", 1000], ["Beta", 2000], ["Gamma", 3000]]
        sheet = SheetData(title="Sales", columns=columns, rows=rows)
        output = tmp_path / "summary_sum.xlsx"
        generate_xlsx([sheet], get_theme("paper"), output, summary=SummaryType.SUM)
        assert output.exists()
        assert output.stat().st_size > 0

    def test_summary_avg(self, tmp_path):
        from src.models import SheetData, ColumnInfo, ColumnType, SummaryType
        from src.xlsx_generator import generate_xlsx

        columns = [
            ColumnInfo(name="Name", col_type=ColumnType.TEXT, width=15),
            ColumnInfo(name="Score", col_type=ColumnType.FLOAT, width=12, number_format="#,##0.00"),
        ]
        rows = [["A", 80.0], ["B", 90.0], ["C", 70.0]]
        sheet = SheetData(title="Scores", columns=columns, rows=rows)
        output = tmp_path / "summary_avg.xlsx"
        generate_xlsx([sheet], get_theme("paper"), output, summary=SummaryType.AVG)
        assert output.exists()

    def test_summary_all(self, tmp_path):
        from src.models import SheetData, ColumnInfo, ColumnType, SummaryType
        from src.xlsx_generator import generate_xlsx

        columns = [
            ColumnInfo(name="Item", col_type=ColumnType.TEXT, width=15),
            ColumnInfo(name="Amount", col_type=ColumnType.CURRENCY, width=12, number_format="$#,##0.00"),
            ColumnInfo(name="Pct", col_type=ColumnType.PERCENTAGE, width=10, number_format="0.0%"),
        ]
        rows = [
            ["A", 100.0, 0.1],
            ["B", 200.0, 0.2],
            ["C", 300.0, 0.3],
        ]
        sheet = SheetData(title="Mixed", columns=columns, rows=rows)
        output = tmp_path / "summary_all.xlsx"
        generate_xlsx([sheet], get_theme("boardroom"), output, summary=SummaryType.ALL)
        assert output.exists()

    def test_highlight_best_worst(self, tmp_path):
        from src.models import SheetData, ColumnInfo, ColumnType, HighlightType
        from src.xlsx_generator import generate_xlsx

        columns = [
            ColumnInfo(name="Name", col_type=ColumnType.TEXT, width=15),
            ColumnInfo(name="Cost", col_type=ColumnType.INTEGER, width=12, number_format="#,##0"),
        ]
        rows = [["Low", 100], ["Mid", 500], ["High", 900]]
        sheet = SheetData(title="Costs", columns=columns, rows=rows)
        output = tmp_path / "highlight_bw.xlsx"
        generate_xlsx([sheet], get_theme("paper"), output, highlight=HighlightType.BEST_WORST)
        assert output.exists()

    def test_highlight_scale(self, tmp_path):
        from src.models import SheetData, ColumnInfo, ColumnType, HighlightType
        from src.xlsx_generator import generate_xlsx

        columns = [
            ColumnInfo(name="Name", col_type=ColumnType.TEXT, width=15),
            ColumnInfo(name="Score", col_type=ColumnType.FLOAT, width=12, number_format="#,##0.00"),
        ]
        rows = [["A", 10.0], ["B", 50.0], ["C", 90.0]]
        sheet = SheetData(title="Scores", columns=columns, rows=rows)
        output = tmp_path / "highlight_scale.xlsx"
        generate_xlsx([sheet], get_theme("paper"), output, highlight=HighlightType.SCALE)
        assert output.exists()

    def test_summary_and_highlight_together(self, tmp_path):
        from src.models import SheetData, ColumnInfo, ColumnType, SummaryType, HighlightType
        from src.xlsx_generator import generate_xlsx

        columns = [
            ColumnInfo(name="Product", col_type=ColumnType.TEXT, width=15),
            ColumnInfo(name="Revenue", col_type=ColumnType.CURRENCY, width=12, number_format="$#,##0.00"),
        ]
        rows = [["Widget", 1000.0], ["Gadget", 2500.0], ["Gizmo", 750.0]]
        sheet = SheetData(title="Revenue", columns=columns, rows=rows)
        output = tmp_path / "both.xlsx"
        generate_xlsx(
            [sheet], get_theme("boardroom"), output,
            summary=SummaryType.ALL,
            highlight=HighlightType.SCALE,
        )
        assert output.exists()

    def test_text_only_columns_skip_summary(self, tmp_path):
        from src.models import SheetData, ColumnInfo, ColumnType, SummaryType
        from src.xlsx_generator import generate_xlsx

        columns = [
            ColumnInfo(name="Name", col_type=ColumnType.TEXT, width=15),
            ColumnInfo(name="Grade", col_type=ColumnType.TEXT, width=10),
        ]
        rows = [["Alice", "A"], ["Bob", "B"]]
        sheet = SheetData(title="Grades", columns=columns, rows=rows)
        output = tmp_path / "text_only.xlsx"
        generate_xlsx([sheet], get_theme("paper"), output, summary=SummaryType.SUM)
        assert output.exists()
