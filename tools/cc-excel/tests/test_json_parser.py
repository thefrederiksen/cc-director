"""Tests for JSON parser."""

import json
import pytest
from src.parsers.json_parser import parse_json


class TestParseJSON:
    def test_objects_format(self, sample_json_objects):
        sheet = parse_json(sample_json_objects)
        assert len(sheet.columns) == 3
        assert sheet.columns[0].name == "name"
        assert sheet.columns[1].name == "score"
        assert len(sheet.rows) == 3
        assert sheet.rows[0][0] == "Alice"
        assert sheet.rows[0][1] == "95"

    def test_arrays_format(self, sample_json_arrays):
        sheet = parse_json(sample_json_arrays)
        assert len(sheet.columns) == 3
        assert sheet.columns[0].name == "name"
        assert len(sheet.rows) == 3
        assert sheet.rows[0][0] == "Alice"

    def test_json_path_navigation(self, sample_json_nested):
        sheet = parse_json(sample_json_nested, json_path="results")
        assert len(sheet.columns) == 2
        assert sheet.columns[0].name == "id"
        assert len(sheet.rows) == 3

    def test_json_path_dollar_prefix(self, sample_json_nested):
        sheet = parse_json(sample_json_nested, json_path="$.results")
        assert len(sheet.rows) == 3

    def test_file_not_found(self, tmp_dir):
        with pytest.raises(FileNotFoundError):
            parse_json(tmp_dir / "nonexistent.json")

    def test_not_an_array(self, tmp_dir):
        path = tmp_dir / "object.json"
        path.write_text('{"key": "value"}', encoding="utf-8")
        with pytest.raises(ValueError, match="Expected a JSON array"):
            parse_json(path)

    def test_empty_array(self, tmp_dir):
        path = tmp_dir / "empty.json"
        path.write_text("[]", encoding="utf-8")
        with pytest.raises(ValueError, match="empty"):
            parse_json(path)

    def test_invalid_json_path(self, sample_json_nested):
        with pytest.raises(ValueError, match="not found"):
            parse_json(sample_json_nested, json_path="nonexistent")

    def test_null_values_handled(self, tmp_dir):
        data = [{"name": "Alice", "value": None}, {"name": "Bob", "value": 42}]
        path = tmp_dir / "nulls.json"
        path.write_text(json.dumps(data), encoding="utf-8")
        sheet = parse_json(path)
        assert sheet.rows[0][1] == ""  # None -> ""
        assert sheet.rows[1][1] == "42"
