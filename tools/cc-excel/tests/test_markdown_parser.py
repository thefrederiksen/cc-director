"""Tests for Markdown table parser."""

import pytest
from src.parsers.markdown_parser import parse_markdown_tables


class TestParseMarkdownTables:
    def test_single_table(self, sample_markdown_single):
        sheets = parse_markdown_tables(sample_markdown_single)
        assert len(sheets) == 1
        sheet = sheets[0]
        assert len(sheet.columns) == 3
        assert sheet.columns[0].name == "Name"
        assert len(sheet.rows) == 3
        assert sheet.rows[0][0] == "Alice"

    def test_multiple_tables_default_first(self, sample_markdown_multi):
        sheets = parse_markdown_tables(sample_markdown_multi, table_index=0)
        assert len(sheets) == 1
        assert sheets[0].rows[0][1] == "1000"

    def test_multiple_tables_select_second(self, sample_markdown_multi):
        sheets = parse_markdown_tables(sample_markdown_multi, table_index=1)
        assert len(sheets) == 1
        assert sheets[0].rows[0][1] == "1500"

    def test_all_tables(self, sample_markdown_multi):
        sheets = parse_markdown_tables(sample_markdown_multi, all_tables=True)
        assert len(sheets) == 3
        assert sheets[0].title == "Table 1"
        assert sheets[1].title == "Table 2"
        assert sheets[2].title == "Table 3"

    def test_no_tables_raises(self, sample_markdown_no_table):
        with pytest.raises(ValueError, match="No pipe tables"):
            parse_markdown_tables(sample_markdown_no_table)

    def test_table_index_out_of_range(self, sample_markdown_single):
        with pytest.raises(ValueError, match="out of range"):
            parse_markdown_tables(sample_markdown_single, table_index=5)

    def test_file_not_found(self, tmp_dir):
        with pytest.raises(FileNotFoundError):
            parse_markdown_tables(tmp_dir / "missing.md")
