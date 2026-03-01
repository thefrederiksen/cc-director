"""Tests for cc-crawl4ai data models and utility functions.

Tests CrawlOptions, CrawlResult, BatchResult dataclasses,
_parse_viewport() from cli.py, and load_urls_from_file() from batch.py.

No API calls, no browser automation, no crawl4ai imports.
"""

import sys
import json
from pathlib import Path

import pytest

# ---------------------------------------------------------------------------
# Add the package source to sys.path so we can import without crawl4ai
# being installed. We stub out the crawl4ai package first.
# ---------------------------------------------------------------------------
_pkg_src = str(Path(__file__).resolve().parent.parent / "src")

# Provide lightweight stubs for crawl4ai so the real modules can import
_crawl4ai_stub = type(sys)("crawl4ai")
_crawl4ai_stub.__path__ = []
_crawl4ai_stub.AsyncWebCrawler = None
_crawl4ai_stub.BrowserConfig = None
_crawl4ai_stub.CrawlerRunConfig = None

# CacheMode stub (used by _get_cache_mode)
class _CacheModeStub:
    ENABLED = "ENABLED"
    DISABLED = "DISABLED"
    WRITE_ONLY = "WRITE_ONLY"
    READ_ONLY = "READ_ONLY"
    BYPASS = "BYPASS"

_crawl4ai_stub.CacheMode = _CacheModeStub

# Sub-module stubs
_cfs = type(sys)("crawl4ai.content_filter_strategy")
_cfs.BM25ContentFilter = None
_cfs.PruningContentFilter = None
_crawl4ai_stub.content_filter_strategy = _cfs

_mgs = type(sys)("crawl4ai.markdown_generation_strategy")
_mgs.DefaultMarkdownGenerator = None
_crawl4ai_stub.markdown_generation_strategy = _mgs

_es = type(sys)("crawl4ai.extraction_strategy")
_es.JsonCssExtractionStrategy = None
_es.LLMExtractionStrategy = None
_crawl4ai_stub.extraction_strategy = _es

sys.modules.setdefault("crawl4ai", _crawl4ai_stub)
sys.modules.setdefault("crawl4ai.content_filter_strategy", _cfs)
sys.modules.setdefault("crawl4ai.markdown_generation_strategy", _mgs)
sys.modules.setdefault("crawl4ai.extraction_strategy", _es)

# Stub typer / rich so cli.py can be imported without them installed
_typer_stub = type(sys)("typer")
_typer_stub.Typer = lambda **kw: type("T", (), {"command": lambda *a, **k: lambda f: f, "callback": lambda *a, **k: lambda f: f, "add_typer": lambda *a, **k: None})()
_typer_stub.Option = lambda *a, **k: None
_typer_stub.Argument = lambda *a, **k: None
_typer_stub.Exit = SystemExit

class _typer_core_stub:
    pass

sys.modules.setdefault("typer", _typer_stub)
sys.modules.setdefault("typer.core", _typer_core_stub)

_rich_console_stub = type(sys)("rich.console")
_rich_console_stub.Console = lambda **kw: type("C", (), {"print": lambda *a, **k: None, "status": lambda *a, **k: type("S", (), {"__enter__": lambda s: s, "__exit__": lambda *a: None})()})()
sys.modules.setdefault("rich", type(sys)("rich"))
sys.modules.setdefault("rich.console", _rich_console_stub)
sys.modules.setdefault("rich.table", type(sys)("rich.table"))
sys.modules.setdefault("rich.progress", type(sys)("rich.progress"))
for attr in ("Table", "Progress", "SpinnerColumn", "TextColumn", "BarColumn", "TaskProgressColumn"):
    setattr(sys.modules["rich.table"] if attr == "Table" else sys.modules["rich.progress"], attr, lambda *a, **k: None)

if _pkg_src not in sys.path:
    sys.path.insert(0, _pkg_src)

# Now import the modules under test
from crawler import CrawlOptions, CrawlResult, _extract_links, _extract_media, _get_content_by_format
from batch import BatchResult, load_urls_from_file
from cli import _parse_viewport


# =========================================================================
# CrawlOptions defaults
# =========================================================================
class TestCrawlOptionsDefaults:
    """Verify every default value on a freshly-created CrawlOptions."""

    def test_output_format(self):
        opts = CrawlOptions()
        assert opts.output_format == "markdown"

    def test_fit_markdown(self):
        opts = CrawlOptions()
        assert opts.fit_markdown is False

    def test_query_is_none(self):
        opts = CrawlOptions()
        assert opts.query is None

    def test_browser(self):
        opts = CrawlOptions()
        assert opts.browser == "chromium"

    def test_stealth(self):
        opts = CrawlOptions()
        assert opts.stealth is False

    def test_proxy_is_none(self):
        opts = CrawlOptions()
        assert opts.proxy is None

    def test_viewport_width(self):
        opts = CrawlOptions()
        assert opts.viewport_width == 1920

    def test_viewport_height(self):
        opts = CrawlOptions()
        assert opts.viewport_height == 1080

    def test_headless(self):
        opts = CrawlOptions()
        assert opts.headless is True

    def test_timeout(self):
        opts = CrawlOptions()
        assert opts.timeout == 30000

    def test_text_only(self):
        opts = CrawlOptions()
        assert opts.text_only is False

    def test_session_is_none(self):
        opts = CrawlOptions()
        assert opts.session is None

    def test_save_session_is_none(self):
        opts = CrawlOptions()
        assert opts.save_session is None

    def test_wait_for_is_none(self):
        opts = CrawlOptions()
        assert opts.wait_for is None

    def test_wait_until(self):
        opts = CrawlOptions()
        assert opts.wait_until == "domcontentloaded"

    def test_scroll(self):
        opts = CrawlOptions()
        assert opts.scroll is False

    def test_scroll_delay(self):
        opts = CrawlOptions()
        assert opts.scroll_delay == 500

    def test_lazy_load(self):
        opts = CrawlOptions()
        assert opts.lazy_load is False

    def test_execute_js_is_none(self):
        opts = CrawlOptions()
        assert opts.execute_js is None

    def test_remove_overlays(self):
        opts = CrawlOptions()
        assert opts.remove_overlays is False

    def test_css_selector_is_none(self):
        opts = CrawlOptions()
        assert opts.css_selector is None

    def test_xpath_is_none(self):
        opts = CrawlOptions()
        assert opts.xpath is None

    def test_schema_path_is_none(self):
        opts = CrawlOptions()
        assert opts.schema_path is None

    def test_llm_extract(self):
        opts = CrawlOptions()
        assert opts.llm_extract is False

    def test_llm_model(self):
        opts = CrawlOptions()
        assert opts.llm_model == "gpt-4o-mini"

    def test_llm_prompt_is_none(self):
        opts = CrawlOptions()
        assert opts.llm_prompt is None

    def test_chunk_strategy_is_none(self):
        opts = CrawlOptions()
        assert opts.chunk_strategy is None

    def test_screenshot(self):
        opts = CrawlOptions()
        assert opts.screenshot is False

    def test_screenshot_path_is_none(self):
        opts = CrawlOptions()
        assert opts.screenshot_path is None

    def test_pdf(self):
        opts = CrawlOptions()
        assert opts.pdf is False

    def test_pdf_path_is_none(self):
        opts = CrawlOptions()
        assert opts.pdf_path is None

    def test_extract_media(self):
        opts = CrawlOptions()
        assert opts.extract_media is False

    def test_media_dir_is_none(self):
        opts = CrawlOptions()
        assert opts.media_dir is None

    def test_cache(self):
        opts = CrawlOptions()
        assert opts.cache == "on"

    def test_cache_dir_is_none(self):
        opts = CrawlOptions()
        assert opts.cache_dir is None

    def test_extract_links(self):
        opts = CrawlOptions()
        assert opts.extract_links is False

    def test_internal_links_only(self):
        opts = CrawlOptions()
        assert opts.internal_links_only is False


class TestCrawlOptionsCustom:
    """Verify custom values can be set."""

    def test_custom_viewport(self):
        opts = CrawlOptions(viewport_width=800, viewport_height=600)
        assert opts.viewport_width == 800
        assert opts.viewport_height == 600

    def test_custom_format(self):
        opts = CrawlOptions(output_format="json")
        assert opts.output_format == "json"

    def test_custom_session(self):
        opts = CrawlOptions(session="my-session")
        assert opts.session == "my-session"


# =========================================================================
# CrawlResult defaults and creation
# =========================================================================
class TestCrawlResult:
    """Verify CrawlResult dataclass."""

    def test_required_fields(self):
        r = CrawlResult(url="https://example.com", success=True)
        assert r.url == "https://example.com"
        assert r.success is True

    def test_content_default(self):
        r = CrawlResult(url="https://example.com", success=True)
        assert r.content == ""

    def test_error_default_none(self):
        r = CrawlResult(url="https://example.com", success=False)
        assert r.error is None

    def test_status_code_default_none(self):
        r = CrawlResult(url="https://example.com", success=True)
        assert r.status_code is None

    def test_links_default_empty(self):
        r = CrawlResult(url="https://example.com", success=True)
        assert r.links == []

    def test_media_default_empty(self):
        r = CrawlResult(url="https://example.com", success=True)
        assert r.media == []

    def test_screenshot_path_default_none(self):
        r = CrawlResult(url="https://example.com", success=True)
        assert r.screenshot_path is None

    def test_pdf_path_default_none(self):
        r = CrawlResult(url="https://example.com", success=True)
        assert r.pdf_path is None

    def test_extracted_data_default_none(self):
        r = CrawlResult(url="https://example.com", success=True)
        assert r.extracted_data is None

    def test_metadata_default_empty(self):
        r = CrawlResult(url="https://example.com", success=True)
        assert r.metadata == {}

    def test_links_list_not_shared(self):
        """Each instance must have its own list (mutable default)."""
        r1 = CrawlResult(url="a", success=True)
        r2 = CrawlResult(url="b", success=True)
        r1.links.append("link")
        assert r2.links == []

    def test_metadata_dict_not_shared(self):
        r1 = CrawlResult(url="a", success=True)
        r2 = CrawlResult(url="b", success=True)
        r1.metadata["key"] = "val"
        assert r2.metadata == {}

    def test_full_creation(self):
        r = CrawlResult(
            url="https://example.com",
            success=True,
            content="# Title",
            status_code=200,
            links=["https://link.com"],
            media=["https://img.com/pic.png"],
            screenshot_path="/tmp/shot.png",
            pdf_path="/tmp/page.pdf",
            extracted_data={"key": "value"},
            metadata={"title": "Example"},
        )
        assert r.content == "# Title"
        assert r.status_code == 200
        assert len(r.links) == 1
        assert len(r.media) == 1
        assert r.screenshot_path == "/tmp/shot.png"
        assert r.pdf_path == "/tmp/page.pdf"
        assert r.extracted_data == {"key": "value"}
        assert r.metadata["title"] == "Example"

    def test_error_result(self):
        r = CrawlResult(url="https://bad.com", success=False, error="Connection refused")
        assert r.success is False
        assert r.error == "Connection refused"


# =========================================================================
# BatchResult defaults
# =========================================================================
class TestBatchResult:
    """Verify BatchResult dataclass."""

    def test_required_fields(self):
        br = BatchResult(total=10, successful=8, failed=2)
        assert br.total == 10
        assert br.successful == 8
        assert br.failed == 2

    def test_results_default_empty(self):
        br = BatchResult(total=0, successful=0, failed=0)
        assert br.results == []

    def test_start_time_default(self):
        br = BatchResult(total=0, successful=0, failed=0)
        assert br.start_time == ""

    def test_end_time_default(self):
        br = BatchResult(total=0, successful=0, failed=0)
        assert br.end_time == ""

    def test_duration_seconds_default(self):
        br = BatchResult(total=0, successful=0, failed=0)
        assert br.duration_seconds == 0

    def test_results_list_not_shared(self):
        br1 = BatchResult(total=0, successful=0, failed=0)
        br2 = BatchResult(total=0, successful=0, failed=0)
        br1.results.append("x")
        assert br2.results == []

    def test_full_creation(self):
        cr = CrawlResult(url="https://example.com", success=True, content="OK")
        br = BatchResult(
            total=1,
            successful=1,
            failed=0,
            results=[cr],
            start_time="2026-01-01T00:00:00",
            end_time="2026-01-01T00:00:05",
            duration_seconds=5.0,
        )
        assert len(br.results) == 1
        assert br.results[0].url == "https://example.com"
        assert br.duration_seconds == 5.0


# =========================================================================
# _parse_viewport
# =========================================================================
class TestParseViewport:
    """Test the _parse_viewport helper from cli.py."""

    def test_valid_1920x1080(self):
        w, h = _parse_viewport("1920x1080")
        assert w == 1920
        assert h == 1080

    def test_valid_800x600(self):
        w, h = _parse_viewport("800x600")
        assert w == 800
        assert h == 600

    def test_valid_1280x720(self):
        w, h = _parse_viewport("1280x720")
        assert w == 1280
        assert h == 720

    def test_valid_small(self):
        w, h = _parse_viewport("320x240")
        assert w == 320
        assert h == 240

    def test_invalid_no_x(self):
        """Missing 'x' separator should raise SystemExit (typer.Exit)."""
        with pytest.raises(SystemExit):
            _parse_viewport("1920-1080")

    def test_invalid_empty(self):
        with pytest.raises((SystemExit, ValueError)):
            _parse_viewport("")

    def test_invalid_single_number(self):
        with pytest.raises(SystemExit):
            _parse_viewport("1920")

    def test_invalid_non_numeric(self):
        with pytest.raises(SystemExit):
            _parse_viewport("widexhigh")

    def test_invalid_triple(self):
        with pytest.raises(SystemExit):
            _parse_viewport("100x200x300")


# =========================================================================
# load_urls_from_file
# =========================================================================
class TestLoadUrlsFromFile:
    """Test load_urls_from_file from batch.py using tmp_path."""

    def test_loads_simple_urls(self, tmp_path):
        f = tmp_path / "urls.txt"
        f.write_text("https://example.com\nhttps://other.com\n")
        urls = load_urls_from_file(f)
        assert urls == ["https://example.com", "https://other.com"]

    def test_skips_comments(self, tmp_path):
        f = tmp_path / "urls.txt"
        f.write_text("# This is a comment\nhttps://example.com\n# Another comment\n")
        urls = load_urls_from_file(f)
        assert urls == ["https://example.com"]

    def test_skips_blank_lines(self, tmp_path):
        f = tmp_path / "urls.txt"
        f.write_text("https://one.com\n\n\nhttps://two.com\n\n")
        urls = load_urls_from_file(f)
        assert urls == ["https://one.com", "https://two.com"]

    def test_skips_comments_and_blanks_mixed(self, tmp_path):
        content = (
            "# header comment\n"
            "\n"
            "https://alpha.com\n"
            "# middle comment\n"
            "\n"
            "https://beta.com\n"
            "\n"
        )
        f = tmp_path / "urls.txt"
        f.write_text(content)
        urls = load_urls_from_file(f)
        assert urls == ["https://alpha.com", "https://beta.com"]

    def test_strips_whitespace(self, tmp_path):
        f = tmp_path / "urls.txt"
        f.write_text("  https://example.com  \n  https://other.com  \n")
        urls = load_urls_from_file(f)
        assert urls == ["https://example.com", "https://other.com"]

    def test_empty_file_returns_empty(self, tmp_path):
        f = tmp_path / "urls.txt"
        f.write_text("")
        urls = load_urls_from_file(f)
        assert urls == []

    def test_only_comments_returns_empty(self, tmp_path):
        f = tmp_path / "urls.txt"
        f.write_text("# comment 1\n# comment 2\n")
        urls = load_urls_from_file(f)
        assert urls == []

    def test_single_url(self, tmp_path):
        f = tmp_path / "urls.txt"
        f.write_text("https://solo.com\n")
        urls = load_urls_from_file(f)
        assert urls == ["https://solo.com"]


# =========================================================================
# _extract_links helper
# =========================================================================
class TestExtractLinks:
    """Test _extract_links from crawler.py."""

    def test_links_as_dict(self):
        """When result.links is a dict with internal/external keys."""

        class FakeResult:
            links = {"internal": ["/page1"], "external": ["https://ext.com"]}

        result = _extract_links(FakeResult())
        assert result == ["/page1", "https://ext.com"]

    def test_links_as_list(self):
        class FakeResult:
            links = ["https://a.com", "https://b.com"]

        result = _extract_links(FakeResult())
        assert result == ["https://a.com", "https://b.com"]

    def test_links_none(self):
        class FakeResult:
            links = None

        result = _extract_links(FakeResult())
        assert result == []

    def test_no_links_attr(self):
        class FakeResult:
            pass

        result = _extract_links(FakeResult())
        assert result == []

    def test_links_empty_dict(self):
        class FakeResult:
            links = {}

        result = _extract_links(FakeResult())
        assert result == []


# =========================================================================
# _extract_media helper
# =========================================================================
class TestExtractMedia:
    """Test _extract_media from crawler.py."""

    def test_media_as_dict(self):
        class FakeResult:
            media = {"images": ["img1.png", "img2.jpg"]}

        result = _extract_media(FakeResult())
        assert result == ["img1.png", "img2.jpg"]

    def test_media_as_list(self):
        class FakeResult:
            media = ["vid.mp4"]

        result = _extract_media(FakeResult())
        assert result == ["vid.mp4"]

    def test_media_none(self):
        class FakeResult:
            media = None

        result = _extract_media(FakeResult())
        assert result == []

    def test_no_media_attr(self):
        class FakeResult:
            pass

        result = _extract_media(FakeResult())
        assert result == []


# =========================================================================
# _get_content_by_format helper
# =========================================================================
class TestGetContentByFormat:
    """Test _get_content_by_format from crawler.py (subset of formats)."""

    def _make_result(self, raw_md="# Title", html="<h1>Title</h1>",
                     cleaned_html="<h1>Title</h1>", raw_html="<html><h1>Title</h1></html>"):
        """Create a minimal fake result object."""

        class FakeMd:
            def __init__(self, raw):
                self.raw_markdown = raw
                self.fit_markdown = None

        class FakeResult:
            pass

        r = FakeResult()
        r.markdown = FakeMd(raw_md)
        r.html = html
        r.cleaned_html = cleaned_html
        r.raw_html = raw_html
        r.metadata = {"title": "Test"}
        return r

    def test_markdown_format(self):
        r = self._make_result()
        opts = CrawlOptions(output_format="markdown")
        content = _get_content_by_format(r, "https://example.com", opts)
        assert content == "# Title"

    def test_html_format(self):
        r = self._make_result()
        opts = CrawlOptions(output_format="html")
        content = _get_content_by_format(r, "https://example.com", opts)
        assert content == "<h1>Title</h1>"

    def test_cleaned_format(self):
        r = self._make_result(cleaned_html="<h1>Clean</h1>")
        opts = CrawlOptions(output_format="cleaned")
        content = _get_content_by_format(r, "https://example.com", opts)
        assert content == "<h1>Clean</h1>"

    def test_raw_format(self):
        r = self._make_result(raw_html="<html>RAW</html>")
        opts = CrawlOptions(output_format="raw")
        content = _get_content_by_format(r, "https://example.com", opts)
        assert content == "<html>RAW</html>"

    def test_json_format(self):
        r = self._make_result()
        opts = CrawlOptions(output_format="json")
        content = _get_content_by_format(r, "https://example.com", opts)
        data = json.loads(content)
        assert data["url"] == "https://example.com"
        assert data["title"] == "Test"
        assert "markdown" in data
