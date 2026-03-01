"""Data models for workbook spec (from-spec subcommand)."""

from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Optional, Union


class StyleType(Enum):
    """Built-in cell/row styles derived from the active theme."""
    HEADER = "header"
    SUBHEADER = "subheader"
    TITLE = "title"
    SUBTITLE = "subtitle"
    INPUT = "input"
    TOTAL = "total"
    BEST = "best"
    WORST = "worst"
    ACCENT = "accent"


@dataclass
class CellSpec:
    """Specification for a single cell in a workbook spec row.

    A cell can hold a static value, a formula, or both (value as cached display).
    """
    value: Any = None
    formula: Optional[str] = None
    number_format: Optional[str] = None
    style: Optional[StyleType] = None
    merge: int = 0
    comment: Optional[str] = None


@dataclass
class ConditionalFormatSpec:
    """Specification for a conditional formatting rule on a sheet."""
    range: str
    type: str
    criteria: Optional[str] = None
    value: Any = None
    min_color: Optional[str] = None
    mid_color: Optional[str] = None
    max_color: Optional[str] = None
    bg_color: Optional[str] = None
    font_color: Optional[str] = None
    format_properties: Optional[dict] = None


@dataclass
class DataValidationSpec:
    """Specification for a data validation rule on a sheet."""
    range: str
    type: str
    criteria: Optional[str] = None
    value: Any = None
    minimum: Any = None
    maximum: Any = None
    source: Optional[list[str]] = None
    input_title: Optional[str] = None
    input_message: Optional[str] = None
    error_title: Optional[str] = None
    error_message: Optional[str] = None


@dataclass
class RowSpec:
    """Specification for a single row in a workbook spec sheet.

    Can be a data row (with cells), a merged title row, or null (spacer).
    """
    cells: Optional[list[Union[CellSpec, Any]]] = None
    style: Optional[StyleType] = None
    merge: int = 0
    value: Any = None
    height: Optional[float] = None


@dataclass
class SheetSpec:
    """Specification for a single worksheet in a workbook spec."""
    name: str
    rows: list[Optional[RowSpec]] = field(default_factory=list)
    columns: Optional[list[float]] = None
    freeze: Optional[list[int]] = None
    autofilter: Any = None
    conditional_formats: list[ConditionalFormatSpec] = field(default_factory=list)
    data_validations: list[DataValidationSpec] = field(default_factory=list)


@dataclass
class WorkbookSpec:
    """Top-level specification for a complete workbook."""
    sheets: list[SheetSpec] = field(default_factory=list)
    theme: Optional[str] = None
    named_ranges: dict[str, str] = field(default_factory=dict)
