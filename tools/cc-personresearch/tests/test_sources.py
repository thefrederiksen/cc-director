"""Tests for source base class and individual sources."""

import pytest
from unittest.mock import patch, MagicMock
from src.sources.base import BaseSource, PUBLIC_EMAIL_DOMAINS
from src.models import SourceResult


class ConcreteSource(BaseSource):
    """Concrete implementation for testing."""
    name = "test_source"
    requires_browser = False

    def fetch(self) -> SourceResult:
        return SourceResult(source=self.name, status="found", data={"test": True})


class ErrorSource(BaseSource):
    """Source that always raises."""
    name = "error_source"
    requires_browser = False

    def fetch(self) -> SourceResult:
        raise ValueError("test error")


class TestBaseSource:
    def test_first_name(self):
        src = ConcreteSource(person_name="John Smith")
        assert src._first_name() == "John"

    def test_last_name(self):
        src = ConcreteSource(person_name="John Smith")
        assert src._last_name() == "Smith"

    def test_first_name_single(self):
        src = ConcreteSource(person_name="Madonna")
        assert src._first_name() == "Madonna"

    def test_last_name_single(self):
        src = ConcreteSource(person_name="Madonna")
        assert src._last_name() == "Madonna"

    def test_email_domain(self):
        src = ConcreteSource(person_name="John", email="john@acme.com")
        assert src._email_domain() == "acme.com"

    def test_email_domain_none(self):
        src = ConcreteSource(person_name="John")
        assert src._email_domain() is None

    def test_run_returns_result(self):
        src = ConcreteSource(person_name="John")
        result = src.run()
        assert result.status == "found"
        assert result.query_time_ms >= 0

    def test_run_catches_errors(self):
        src = ErrorSource(person_name="John")
        result = src.run()
        assert result.status == "error"
        assert "test error" in result.error_message

    def test_three_name_parts(self):
        src = ConcreteSource(person_name="John Michael Smith")
        assert src._first_name() == "John"
        assert src._last_name() == "Smith"


class TestContextCompany:
    def test_extracts_company_from_corporate_email(self):
        src = ConcreteSource(person_name="Jane", email="jane@acmecorp.com")
        assert src._context_company() == "acmecorp"

    def test_returns_none_for_public_domain(self):
        src = ConcreteSource(person_name="James", email="james@gmail.com")
        assert src._context_company() is None

    def test_returns_none_without_email(self):
        src = ConcreteSource(person_name="James")
        assert src._context_company() is None

    def test_strips_tld(self):
        src = ConcreteSource(person_name="James", email="james@avigilon.com")
        assert src._context_company() == "avigilon"

    def test_handles_subdomain(self):
        src = ConcreteSource(person_name="James", email="james@corp.bigco.com")
        assert src._context_company() == "corp"

    def test_lowercases(self):
        src = ConcreteSource(person_name="Jane", email="jane@AcmeCorp.com")
        assert src._context_company() == "acmecorp"


class TestGetPageText:
    def test_returns_text_when_browser_available(self):
        mock_browser = MagicMock()
        mock_browser.text.return_value = {"text": "Hello world page content"}
        src = ConcreteSource(person_name="Test", browser=mock_browser)
        assert src._get_page_text() == "Hello world page content"

    def test_returns_empty_without_browser(self):
        src = ConcreteSource(person_name="Test")
        assert src._get_page_text() == ""

    def test_returns_empty_on_error(self):
        mock_browser = MagicMock()
        mock_browser.text.side_effect = Exception("connection lost")
        src = ConcreteSource(person_name="Test", browser=mock_browser)
        assert src._get_page_text() == ""


class TestPublicEmailDomains:
    def test_common_public_domains(self):
        for domain in ["gmail.com", "yahoo.com", "hotmail.com", "outlook.com"]:
            assert domain in PUBLIC_EMAIL_DOMAINS


class TestGravatarSource:
    def test_skips_without_email(self):
        from src.sources.gravatar import GravatarSource
        src = GravatarSource(person_name="Test")
        result = src.fetch()
        assert result.status == "skipped"

    @patch("src.sources.gravatar.httpx.get")
    def test_not_found(self, mock_get):
        from src.sources.gravatar import GravatarSource
        mock_resp = MagicMock()
        mock_resp.status_code = 404
        mock_get.return_value = mock_resp

        src = GravatarSource(person_name="Test", email="nobody@nowhere.com")
        result = src.fetch()
        assert result.status == "not_found"


class TestWhoisSource:
    def test_skips_without_email(self):
        from src.sources.whois_lookup import WhoisSource
        src = WhoisSource(person_name="Test")
        result = src.fetch()
        assert result.status == "skipped"

    def test_skips_public_domains(self):
        from src.sources.whois_lookup import WhoisSource
        src = WhoisSource(person_name="Test", email="test@gmail.com")
        result = src.fetch()
        assert result.status == "skipped"
        assert "Public email domain" in result.error_message


class TestFECSource:
    @patch("src.sources.fec_donations.httpx.get")
    def test_rate_limited(self, mock_get):
        from src.sources.fec_donations import FECSource
        mock_resp = MagicMock()
        mock_resp.status_code = 429
        mock_get.return_value = mock_resp

        src = FECSource(person_name="Test")
        result = src.fetch()
        assert result.status == "error"
        assert "rate limit" in result.error_message.lower()

    @patch("src.sources.fec_donations.httpx.get")
    def test_not_found(self, mock_get):
        from src.sources.fec_donations import FECSource
        mock_resp = MagicMock()
        mock_resp.status_code = 200
        mock_resp.json.return_value = {"results": [], "pagination": {"count": 0}}
        mock_get.return_value = mock_resp

        src = FECSource(person_name="Nonexistent Person ZZZZ")
        result = src.fetch()
        assert result.status == "not_found"

    @patch("src.sources.fec_donations.httpx.get")
    def test_context_filtering(self, mock_get):
        from src.sources.fec_donations import FECSource
        mock_resp = MagicMock()
        mock_resp.status_code = 200
        mock_resp.json.return_value = {
            "results": [
                {
                    "contributor_name": "JANE DOE",
                    "contribution_receipt_date": "2024-01-01",
                    "contribution_receipt_amount": 500,
                    "committee": {"name": "Some PAC"},
                    "contributor_employer": "ACME CORP",
                    "contributor_occupation": "CEO",
                    "contributor_city": "AUSTIN",
                    "contributor_state": "TX",
                    "contributor_zip": "78701",
                    "contributor_street_1": "123 Main St",
                },
                {
                    "contributor_name": "JANE DOE",
                    "contribution_receipt_date": "2024-02-01",
                    "contribution_receipt_amount": 250,
                    "committee": {"name": "Another PAC"},
                    "contributor_employer": "UNRELATED CORP",
                    "contributor_occupation": "MANAGER",
                    "contributor_city": "NEW YORK",
                    "contributor_state": "NY",
                    "contributor_zip": "10001",
                    "contributor_street_1": "456 Oak Ave",
                },
            ],
            "pagination": {"count": 2},
        }
        mock_get.return_value = mock_resp

        src = FECSource(person_name="Jane Doe", email="jane@acmecorp.com")
        result = src.fetch()
        assert result.status == "found"
        # First result should be the context match
        assert result.data["contributions"][0]["context_match"] is True
        assert result.data["contributions"][1]["context_match"] is False
        assert result.data["context_matches"] == 1


class TestGitHubSource:
    @patch("src.sources.github_search.httpx.get")
    def test_context_filtering_users(self, mock_get):
        from src.sources.github_search import GitHubSource

        def side_effect(url, **kwargs):
            mock_resp = MagicMock()
            if "search/commits" in url:
                mock_resp.status_code = 200
                mock_resp.json.return_value = {"items": []}
            elif "search/users" in url:
                mock_resp.status_code = 200
                mock_resp.json.return_value = {
                    "items": [
                        {"login": "jhenderson", "html_url": "https://github.com/jhenderson",
                         "avatar_url": "", "type": "User",
                         "url": "https://api.github.com/users/jhenderson"},
                    ]
                }
            elif "users/jhenderson" in url:
                mock_resp.status_code = 200
                mock_resp.json.return_value = {
                    "name": "Jane Doe", "company": "acmecorp",
                    "location": "Austin", "bio": "CEO at acmecorp",
                    "public_repos": 5, "email": None, "blog": "",
                }
            else:
                mock_resp.status_code = 404
            return mock_resp

        mock_get.side_effect = side_effect

        src = GitHubSource(person_name="Jane Doe", email="jane@acmecorp.com")
        result = src.fetch()
        assert result.status == "found"
        assert result.data["users"][0]["context_match"] is True


class TestLinkedInSource:
    @patch("src.sources.linkedin.subprocess.run")
    def test_handles_missing_cli(self, mock_run):
        from src.sources.linkedin import LinkedInSource
        mock_run.side_effect = FileNotFoundError("cc-linkedin not found")

        src = LinkedInSource(person_name="Test")
        result = src.fetch()
        assert result.status == "not_found"

    @patch("src.sources.linkedin.subprocess.run")
    def test_handles_timeout(self, mock_run):
        from src.sources.linkedin import LinkedInSource
        import subprocess
        mock_run.side_effect = subprocess.TimeoutExpired(cmd="cc-linkedin", timeout=90)

        src = LinkedInSource(person_name="Test")
        result = src.fetch()
        assert result.status == "not_found"

    @patch("src.sources.linkedin.subprocess.run")
    def test_searches_with_company_context(self, mock_run):
        from src.sources.linkedin import LinkedInSource

        # First call (with company): return empty
        # Second call (name only): return empty
        mock_result = MagicMock()
        mock_result.returncode = 0
        mock_result.stdout = "[]"
        mock_result.stderr = ""
        mock_run.return_value = mock_result

        src = LinkedInSource(person_name="Jane Doe", email="jane@acmecorp.com")
        result = src.fetch()

        # Should have called search at least twice (company + name fallback)
        assert mock_run.call_count >= 1
        # First call should include "mindzie" in the query arg (after "search" subcommand)
        first_call_args = mock_run.call_args_list[0][0][0]
        search_idx = first_call_args.index("search")
        query_arg = first_call_args[search_idx + 1]
        assert "acmecorp" in query_arg.lower()

    @patch("src.sources.linkedin.subprocess.run")
    def test_passes_workspace(self, mock_run):
        from src.sources.linkedin import LinkedInSource

        mock_result = MagicMock()
        mock_result.returncode = 0
        mock_result.stdout = "[]"
        mock_result.stderr = ""
        mock_run.return_value = mock_result

        src = LinkedInSource(person_name="Test", linkedin_workspace="my-linkedin")
        result = src.fetch()

        # Check --workspace flag was passed as global option (before subcommand)
        call_args = mock_run.call_args_list[0][0][0]
        ws_idx = call_args.index("--workspace")
        assert call_args[ws_idx + 1] == "my-linkedin"
        # Global options must come before the "search" subcommand
        search_idx = call_args.index("search")
        assert ws_idx < search_idx


class TestBrowserSources:
    """Test that browser sources skip correctly without browser."""

    def test_google_dorking_skips(self):
        from src.sources.google_dorking import GoogleDorkingSource
        src = GoogleDorkingSource(person_name="Test")
        result = src.fetch()
        assert result.status == "skipped"

    def test_thatsthem_skips(self):
        from src.sources.thatsthem import ThatSThemSource
        src = ThatSThemSource(person_name="Test")
        result = src.fetch()
        assert result.status == "skipped"

    def test_truepeoplesearch_skips(self):
        from src.sources.truepeoplesearch import TruePeopleSearchSource
        src = TruePeopleSearchSource(person_name="Test")
        result = src.fetch()
        assert result.status == "skipped"

    def test_zabasearch_skips(self):
        from src.sources.zabasearch import ZabaSearchSource
        src = ZabaSearchSource(person_name="Test")
        result = src.fetch()
        assert result.status == "skipped"

    def test_nuwber_skips(self):
        from src.sources.nuwber import NuwberSource
        src = NuwberSource(person_name="Test")
        result = src.fetch()
        assert result.status == "skipped"

    def test_company_website_skips_no_browser(self):
        from src.sources.company_website import CompanyWebsiteSource
        src = CompanyWebsiteSource(person_name="Test", email="test@acme.com")
        result = src.fetch()
        assert result.status == "skipped"

    def test_company_website_skips_public_domain(self):
        from src.sources.company_website import CompanyWebsiteSource
        mock_browser = MagicMock()
        src = CompanyWebsiteSource(person_name="Test", email="test@gmail.com", browser=mock_browser)
        result = src.fetch()
        assert result.status == "skipped"
        assert "Public email domain" in result.error_message

    def test_opencorporates_skips(self):
        from src.sources.opencorporates import OpenCorporatesSource
        src = OpenCorporatesSource(person_name="Test")
        result = src.fetch()
        assert result.status == "skipped"
