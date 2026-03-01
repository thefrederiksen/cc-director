"""Data models for cc-excel."""

from dataclasses import dataclass, field
from enum import Enum
from typing import Optional


class ColumnType(Enum):
    """Detected column data type."""
    TEXT = "text"
    INTEGER = "integer"
    FLOAT = "float"
    PERCENTAGE = "percentage"
    CURRENCY = "currency"
    DATE = "date"
    BOOLEAN = "boolean"


@dataclass
class ColumnInfo:
    """Metadata for a single column."""
    name: str
    col_type: ColumnType = ColumnType.TEXT
    width: float = 12.0
    number_format: str = ""


@dataclass
class SheetData:
    """Parsed tabular data ready for Excel generation."""
    title: str
    columns: list[ColumnInfo] = field(default_factory=list)
    rows: list[list[object]] = field(default_factory=list)
    source_file: str = ""


class SummaryType(Enum):
    """Summary row type for tabular data."""
    SUM = "sum"
    AVG = "avg"
    ALL = "all"


class HighlightType(Enum):
    """Conditional highlighting mode for numeric columns."""
    BEST_WORST = "best-worst"
    SCALE = "scale"


class ChartType(Enum):
    """Supported chart types."""
    BAR = "bar"
    LINE = "line"
    PIE = "pie"
    COLUMN = "column"


@dataclass
class ChartSpec:
    """Specification for a chart to embed."""
    chart_type: ChartType
    title: str
    category_column: int
    value_columns: list[int] = field(default_factory=list)
    sheet_name: str = "Chart"
