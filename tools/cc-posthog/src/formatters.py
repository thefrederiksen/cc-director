"""Output formatting: Rich tables, JSON, CSV."""

import csv
import io
import json
from typing import Any

from rich.console import Console
from rich.table import Table

console = Console()


def format_table(
    title: str,
    columns: list[str],
    rows: list[list[Any]],
    *,
    as_json: bool = False,
    as_csv: bool = False,
) -> None:
    """Format and print data as a Rich table, JSON, or CSV."""
    if as_json:
        _print_json(columns, rows)
    elif as_csv:
        _print_csv(columns, rows)
    else:
        _print_rich_table(title, columns, rows)


def _print_rich_table(title: str, columns: list[str], rows: list[list[Any]]) -> None:
    """Print a Rich table to the console."""
    table = Table(title=title, show_lines=False)
    for col in columns:
        justify = "right" if col in ("Views", "Count", "Visitors", "Rate",
                                      "Drop-off", "Percentage", "Duration",
                                      "Pages", "Clicks", "Unique Visitors") else "left"
        table.add_column(col, justify=justify)
    for row in rows:
        table.add_row(*[str(v) for v in row])
    console.print(table)


def _print_json(columns: list[str], rows: list[list[Any]]) -> None:
    """Print data as JSON array of objects."""
    data = [dict(zip(columns, row)) for row in rows]
    console.print(json.dumps(data, indent=2, default=str))


def _print_csv(columns: list[str], rows: list[list[Any]]) -> None:
    """Print data as CSV to stdout."""
    buf = io.StringIO()
    writer = csv.writer(buf)
    writer.writerow(columns)
    for row in rows:
        writer.writerow(row)
    console.print(buf.getvalue(), highlight=False)


def format_report_json(report: Any) -> None:
    """Print a full AnalyticsReport as JSON."""
    console.print(report.model_dump_json(indent=2))


def print_info(message: str) -> None:
    """Print an informational message."""
    console.print(f"[bold]{message}[/bold]")


def print_error(message: str) -> None:
    """Print an error message."""
    console.print(f"[bold red]ERROR:[/bold red] {message}")
