"""Shared fixtures for cc-excel tests."""

import json
import tempfile
from pathlib import Path

import pytest


@pytest.fixture
def tmp_dir(tmp_path):
    """Provide a temporary directory."""
    return tmp_path


@pytest.fixture
def sample_csv(tmp_path):
    """Create a sample CSV file with mixed types."""
    content = (
        "Name,Revenue,Growth,Date,Active\n"
        "Alpha Corp,1250000,12.5%,2025-01-15,true\n"
        "Beta Inc,890000,8.3%,2025-02-20,true\n"
        "Gamma LLC,2100000,15.7%,2025-03-10,false\n"
        "Delta Co,560000,3.2%,2025-04-05,true\n"
    )
    path = tmp_path / "test_data.csv"
    path.write_text(content, encoding="utf-8")
    return path


@pytest.fixture
def sample_csv_no_header(tmp_path):
    """Create a CSV file without headers."""
    content = (
        "Alice,95,A\n"
        "Bob,87,B+\n"
        "Charlie,92,A-\n"
    )
    path = tmp_path / "no_header.csv"
    path.write_text(content, encoding="utf-8")
    return path


@pytest.fixture
def sample_csv_semicolon(tmp_path):
    """Create a semicolon-delimited CSV file."""
    content = (
        "Product;Price;Quantity\n"
        "Widget;$19.99;100\n"
        "Gadget;$49.99;50\n"
        "Gizmo;$9.99;200\n"
    )
    path = tmp_path / "semicolon.csv"
    path.write_text(content, encoding="utf-8")
    return path


@pytest.fixture
def sample_json_objects(tmp_path):
    """Create a JSON file with array of objects."""
    data = [
        {"name": "Alice", "score": 95, "grade": "A"},
        {"name": "Bob", "score": 87, "grade": "B+"},
        {"name": "Charlie", "score": 92, "grade": "A-"},
    ]
    path = tmp_path / "objects.json"
    path.write_text(json.dumps(data, indent=2), encoding="utf-8")
    return path


@pytest.fixture
def sample_json_arrays(tmp_path):
    """Create a JSON file with array of arrays."""
    data = [
        ["name", "score", "grade"],
        ["Alice", 95, "A"],
        ["Bob", 87, "B+"],
        ["Charlie", 92, "A-"],
    ]
    path = tmp_path / "arrays.json"
    path.write_text(json.dumps(data, indent=2), encoding="utf-8")
    return path


@pytest.fixture
def sample_json_nested(tmp_path):
    """Create a nested JSON file with a data array."""
    data = {
        "status": "ok",
        "meta": {"total": 3},
        "results": [
            {"id": 1, "value": 100},
            {"id": 2, "value": 200},
            {"id": 3, "value": 300},
        ],
    }
    path = tmp_path / "nested.json"
    path.write_text(json.dumps(data, indent=2), encoding="utf-8")
    return path


@pytest.fixture
def sample_markdown_single(tmp_path):
    """Create a Markdown file with one pipe table."""
    content = (
        "# Report\n\n"
        "Some text before the table.\n\n"
        "| Name | Score | Grade |\n"
        "|------|-------|-------|\n"
        "| Alice | 95 | A |\n"
        "| Bob | 87 | B+ |\n"
        "| Charlie | 92 | A- |\n"
        "\nSome text after.\n"
    )
    path = tmp_path / "single_table.md"
    path.write_text(content, encoding="utf-8")
    return path


@pytest.fixture
def sample_markdown_multi(tmp_path):
    """Create a Markdown file with multiple pipe tables."""
    content = (
        "# Sales Report\n\n"
        "## Q1\n\n"
        "| Product | Revenue |\n"
        "|---------|--------|\n"
        "| Widget | 1000 |\n"
        "| Gadget | 2000 |\n"
        "\n## Q2\n\n"
        "| Product | Revenue |\n"
        "|---------|--------|\n"
        "| Widget | 1500 |\n"
        "| Gadget | 2500 |\n"
        "\n## Q3\n\n"
        "| Product | Revenue |\n"
        "|---------|--------|\n"
        "| Widget | 1800 |\n"
        "| Gadget | 3000 |\n"
    )
    path = tmp_path / "multi_table.md"
    path.write_text(content, encoding="utf-8")
    return path


@pytest.fixture
def sample_markdown_no_table(tmp_path):
    """Create a Markdown file with no tables."""
    content = "# Just a heading\n\nSome paragraph text.\n"
    path = tmp_path / "no_table.md"
    path.write_text(content, encoding="utf-8")
    return path
