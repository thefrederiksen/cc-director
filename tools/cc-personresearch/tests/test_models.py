"""Tests for data models."""

import pytest
from src.models import PersonReport, SearchParams, SourceResult, SearchSummary


class TestSourceResult:
    def test_create_found_result(self):
        result = SourceResult(source="test", status="found", data={"key": "value"})
        assert result.source == "test"
        assert result.status == "found"
        assert result.data["key"] == "value"

    def test_create_error_result(self):
        result = SourceResult(source="test", status="error", error_message="failed")
        assert result.status == "error"
        assert result.error_message == "failed"

    def test_default_query_time(self):
        result = SourceResult(source="test", status="not_found")
        assert result.query_time_ms == 0


class TestPersonReport:
    def test_empty_report(self):
        report = PersonReport(
            search_params=SearchParams(name="Test Person")
        )
        assert report.search_params.name == "Test Person"
        assert len(report.sources) == 0
        assert report.summary.total_sources == 0

    def test_add_found_result(self):
        report = PersonReport(
            search_params=SearchParams(name="Test Person")
        )
        result = SourceResult(source="github", status="found", data={"users": []})
        report.add_result(result)
        assert report.summary.total_sources == 1
        assert report.summary.sources_with_results == 1
        assert "github" in report.sources

    def test_add_error_result(self):
        report = PersonReport(
            search_params=SearchParams(name="Test Person")
        )
        result = SourceResult(source="fec", status="error", error_message="timeout")
        report.add_result(result)
        assert report.summary.total_sources == 1
        assert report.summary.sources_failed == 1

    def test_add_skipped_result(self):
        report = PersonReport(
            search_params=SearchParams(name="Test Person")
        )
        result = SourceResult(source="whois", status="skipped")
        report.add_result(result)
        assert report.summary.sources_skipped == 1

    def test_add_url_dedup(self):
        report = PersonReport(
            search_params=SearchParams(name="Test Person")
        )
        report.add_url("https://github.com/test")
        report.add_url("https://github.com/test")
        report.add_url("https://linkedin.com/in/test")
        assert len(report.discovered_urls) == 2

    def test_add_empty_url(self):
        report = PersonReport(
            search_params=SearchParams(name="Test Person")
        )
        report.add_url("")
        report.add_url(None)
        assert len(report.discovered_urls) == 0

    def test_search_params_timestamp(self):
        params = SearchParams(name="Test", email="test@example.com")
        assert params.timestamp.endswith("Z")
        assert params.name == "Test"
        assert params.email == "test@example.com"

    def test_json_serialization(self):
        report = PersonReport(
            search_params=SearchParams(name="Test Person", email="test@test.com")
        )
        report.add_result(SourceResult(source="github", status="found", data={"users": []}))
        json_str = report.model_dump_json()
        assert "Test Person" in json_str
        assert "github" in json_str


class TestSearchSummary:
    def test_default_note(self):
        summary = SearchSummary()
        assert "Raw OSINT data" in summary.note

    def test_default_counts(self):
        summary = SearchSummary()
        assert summary.total_sources == 0
        assert summary.sources_with_results == 0
        assert summary.sources_failed == 0
