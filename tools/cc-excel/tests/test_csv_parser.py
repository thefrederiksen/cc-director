"""Tests for CSV parser."""

import pytest
from src.parsers.csv_parser import parse_csv


class TestParseCSV:
    def test_basic_parse(self, sample_csv):
        sheet = parse_csv(sample_csv)
        assert sheet.title == "test_data"
        assert len(sheet.columns) == 5
        assert sheet.columns[0].name == "Name"
        assert sheet.columns[1].name == "Revenue"
        assert len(sheet.rows) == 4
        assert sheet.rows[0][0] == "Alpha Corp"

    def test_no_header(self, sample_csv_no_header):
        sheet = parse_csv(sample_csv_no_header, has_header=False)
        assert sheet.columns[0].name == "A"
        assert sheet.columns[1].name == "B"
        assert sheet.columns[2].name == "C"
        assert len(sheet.rows) == 3
        assert sheet.rows[0][0] == "Alice"

    def test_semicolon_delimiter(self, sample_csv_semicolon):
        sheet = parse_csv(sample_csv_semicolon, delimiter=";")
        assert len(sheet.columns) == 3
        assert sheet.columns[0].name == "Product"
        assert sheet.rows[0][0] == "Widget"
        assert sheet.rows[0][1] == "$19.99"

    def test_file_not_found(self, tmp_dir):
        with pytest.raises(FileNotFoundError):
            parse_csv(tmp_dir / "nonexistent.csv")

    def test_empty_file(self, tmp_dir):
        path = tmp_dir / "empty.csv"
        path.write_text("", encoding="utf-8")
        with pytest.raises(ValueError, match="empty"):
            parse_csv(path)

    def test_row_length_normalization(self, tmp_dir):
        content = "A,B,C\n1,2\n4,5,6,7\n"
        path = tmp_dir / "ragged.csv"
        path.write_text(content, encoding="utf-8")
        sheet = parse_csv(path)
        assert len(sheet.rows[0]) == 3  # padded
        assert sheet.rows[0][2] == ""
        assert len(sheet.rows[1]) == 3  # truncated

    def test_source_file_set(self, sample_csv):
        sheet = parse_csv(sample_csv)
        assert sheet.source_file == str(sample_csv)
