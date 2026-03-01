"""Tests for the aggregator module."""

import pytest
from src.aggregator import normalize_phone, normalize_email, normalize_address, extract_all_urls
from src.models import PersonReport, SearchParams, SourceResult


class TestNormalizePhone:
    def test_strips_formatting(self):
        assert normalize_phone("(512) 555-1234") == "5125551234"

    def test_strips_country_code(self):
        assert normalize_phone("+1 (512) 555-1234") == "5125551234"

    def test_plain_digits(self):
        assert normalize_phone("5125551234") == "5125551234"

    def test_dashes_only(self):
        assert normalize_phone("512-555-1234") == "5125551234"


class TestNormalizeEmail:
    def test_lowercase(self):
        assert normalize_email("John@Example.COM") == "john@example.com"

    def test_strips_whitespace(self):
        assert normalize_email("  john@example.com  ") == "john@example.com"


class TestNormalizeAddress:
    def test_street_abbreviation(self):
        result = normalize_address("123 Main St.")
        assert "street" in result

    def test_avenue_abbreviation(self):
        result = normalize_address("456 Oak Ave")
        assert "avenue" in result

    def test_collapses_whitespace(self):
        result = normalize_address("123  Main   Street")
        assert "  " not in result


class TestExtractAllUrls:
    def test_collects_from_urls_field(self):
        report = PersonReport(search_params=SearchParams(name="Test"))
        report.add_result(SourceResult(
            source="github", status="found",
            data={"urls": ["https://github.com/test", "https://github.com/test2"]}
        ))
        urls = extract_all_urls(report)
        assert "https://github.com/test" in urls
        assert "https://github.com/test2" in urls

    def test_collects_profile_url(self):
        report = PersonReport(search_params=SearchParams(name="Test"))
        report.add_result(SourceResult(
            source="gravatar", status="found",
            data={"profile_url": "https://gravatar.com/test"}
        ))
        urls = extract_all_urls(report)
        assert "https://gravatar.com/test" in urls

    def test_skips_error_sources(self):
        report = PersonReport(search_params=SearchParams(name="Test"))
        report.add_result(SourceResult(
            source="fec", status="error",
            data={"urls": ["https://should.not.appear"]}
        ))
        urls = extract_all_urls(report)
        assert len(urls) == 0

    def test_deduplicates(self):
        report = PersonReport(search_params=SearchParams(name="Test"))
        report.add_result(SourceResult(
            source="s1", status="found",
            data={"urls": ["https://example.com"]}
        ))
        report.add_result(SourceResult(
            source="s2", status="found",
            data={"urls": ["https://example.com"]}
        ))
        urls = extract_all_urls(report)
        assert urls.count("https://example.com") == 1
