"""Tests for cc-posthog formatters module."""

import json
import sys
from io import StringIO
from pathlib import Path

import pytest
from rich.console import Console

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent / "src"))
sys.path.insert(0, str(Path(__file__).parent.parent.parent / "cc_shared"))

from src.formatters import _print_csv, _print_json
from src.schema import AnalyticsReport, PageViewRow, StatusInfo, TrafficSource


class TestJsonOutput:

    def test_json_output_structure(self, capsys):
        columns = ["Page", "Views"]
        rows = [["/home", 100], ["/about", 50]]
        # Use a fresh console writing to stdout
        import src.formatters as fmt
        old_console = fmt.console
        fmt.console = Console(file=sys.stdout, force_terminal=False)
        try:
            _print_json(columns, rows)
        finally:
            fmt.console = old_console
        captured = capsys.readouterr()
        data = json.loads(captured.out.strip())
        assert len(data) == 2
        assert data[0]["Page"] == "/home"
        assert data[0]["Views"] == 100
        assert data[1]["Page"] == "/about"
        assert data[1]["Views"] == 50

    def test_json_empty_rows(self, capsys):
        import src.formatters as fmt
        old_console = fmt.console
        fmt.console = Console(file=sys.stdout, force_terminal=False)
        try:
            _print_json(["Col"], [])
        finally:
            fmt.console = old_console
        captured = capsys.readouterr()
        data = json.loads(captured.out.strip())
        assert data == []


class TestCsvOutput:

    def test_csv_output(self, capsys):
        import src.formatters as fmt
        old_console = fmt.console
        fmt.console = Console(file=sys.stdout, force_terminal=False)
        try:
            _print_csv(["Name", "Count"], [["google", 42], ["direct", 10]])
        finally:
            fmt.console = old_console
        captured = capsys.readouterr()
        lines = captured.out.strip().split("\n")
        assert "Name,Count" in lines[0]
        assert "google,42" in lines[1]


class TestAnalyticsReport:

    def test_report_json_serialization(self):
        report = AnalyticsReport(
            project="test",
            period="7d",
            status=StatusInfo(
                project_name="Test",
                project_id=1,
                host="https://us.posthog.com",
                event_count=500,
            ),
            views=[PageViewRow(page="/home", views=100, unique_visitors=80)],
            sources=[TrafficSource(source="google", count=60, percentage=60.0)],
        )
        data = json.loads(report.model_dump_json())
        assert data["project"] == "test"
        assert data["period"] == "7d"
        assert data["status"]["event_count"] == 500
        assert len(data["views"]) == 1
        assert data["views"][0]["page"] == "/home"
        assert len(data["sources"]) == 1
