"""Core crawler module for cc-crawl4ai."""

import asyncio
import json
from pathlib import Path
from typing import Optional, Any, Union
from dataclasses import dataclass, field

from crawl4ai import AsyncWebCrawler, BrowserConfig, CrawlerRunConfig, CacheMode
from crawl4ai.content_filter_strategy import BM25ContentFilter, PruningContentFilter
from crawl4ai.markdown_generation_strategy import DefaultMarkdownGenerator
from crawl4ai.extraction_strategy import (
    JsonCssExtractionStrategy,
    LLMExtractionStrategy,
)

try:
    from .sessions import SessionManager, get_cache_dir
except ImportError:
    from sessions import SessionManager, get_cache_dir


@dataclass
class CrawlOptions:
    """Options for crawling."""
    # Output options
    output_format: str = "markdown"  # markdown, json, html, raw, cleaned
    fit_markdown: bool = False  # Use fit markdown (noise filtered)
    query: Optional[str] = None  # BM25 content filtering

    # Browser options
    browser: str = "chromium"  # chromium, firefox, webkit
    stealth: bool = False  # Evade bot detection
    proxy: Optional[str] = None  # Proxy URL
    viewport_width: int = 1920
    viewport_height: int = 1080
    headless: bool = True
    timeout: int = 30000  # ms
    text_only: bool = False  # Disable images for faster crawl

    # Session options
    session: Optional[str] = None  # Session name to use
    save_session: Optional[str] = None  # Save session after crawl

    # Dynamic content options
    wait_for: Optional[str] = None  # CSS selector to wait for
    wait_until: str = "domcontentloaded"  # domcontentloaded or networkidle
    scroll: bool = False  # Full page scroll
    scroll_delay: int = 500  # ms between scrolls
    lazy_load: bool = False  # Wait for lazy images
    execute_js: Optional[str] = None  # JS to execute before extraction
    remove_overlays: bool = False  # Remove popups/cookie banners

    # Extraction options
    css_selector: Optional[str] = None  # CSS extraction
    xpath: Optional[str] = None  # XPath extraction
    schema_path: Optional[str] = None  # JSON schema for extraction
    llm_extract: bool = False  # LLM-driven extraction
    llm_model: str = "gpt-4o-mini"  # LLM model
    llm_prompt: Optional[str] = None  # LLM extraction prompt
    chunk_strategy: Optional[str] = None  # topic, regex, sliding

    # Media options
    screenshot: bool = False  # Capture screenshot
    screenshot_path: Optional[str] = None
    pdf: bool = False  # Generate PDF
    pdf_path: Optional[str] = None
    extract_media: bool = False  # Extract images/video
    media_dir: Optional[str] = None

    # Cache options
    cache: str = "on"  # on, off, refresh, read-only
    cache_dir: Optional[str] = None

    # Link options
    extract_links: bool = False
    internal_links_only: bool = False  # Exclude external links


@dataclass
class CrawlResult:
    """Result of a crawl operation."""
    url: str
    success: bool
    content: str = ""
    error: Optional[str] = None
    status_code: Optional[int] = None
    links: list[str] = field(default_factory=list)
    media: list[str] = field(default_factory=list)
    screenshot_path: Optional[str] = None
    pdf_path: Optional[str] = None
    extracted_data: Optional[Any] = None
    metadata: dict = field(default_factory=dict)


def _get_cache_mode(cache: str) -> CacheMode:
    """Convert cache string to CacheMode."""
    modes = {
        "on": CacheMode.ENABLED,
        "off": CacheMode.DISABLED,
        "refresh": CacheMode.WRITE_ONLY,
        "read-only": CacheMode.READ_ONLY,
        "bypass": CacheMode.BYPASS,
    }
    return modes.get(cache, CacheMode.ENABLED)


def _build_browser_config(options: CrawlOptions, session_manager: SessionManager) -> BrowserConfig:
    """Build browser configuration."""
    # Get user data dir if using session
    user_data_dir = None
    if options.session:
        profile_path = session_manager.get_profile_path(options.session)
        if profile_path:
            user_data_dir = str(profile_path)

    return BrowserConfig(
        browser_type=options.browser,
        headless=options.headless,
        viewport_width=options.viewport_width,
        viewport_height=options.viewport_height,
        user_data_dir=user_data_dir,
        proxy=options.proxy,
        ignore_https_errors=True,
        enable_stealth=options.stealth,
        text_mode=options.text_only,
    )


def _build_extraction_strategy(options: CrawlOptions) -> Optional[Union[JsonCssExtractionStrategy, LLMExtractionStrategy]]:
    """Build extraction strategy if needed."""
    if options.schema_path:
        with open(options.schema_path) as f:
            schema = json.load(f)
        return JsonCssExtractionStrategy(schema)

    if options.llm_extract:
        # Use LLMExtractionStrategy with instruction
        return LLMExtractionStrategy(
            provider=options.llm_model,
            instruction=options.llm_prompt or "Extract the main content",
        )

    return None


def _build_markdown_generator(options: CrawlOptions) -> Optional[DefaultMarkdownGenerator]:
    """Build markdown generator with optional content filter."""
    if options.query:
        # Use BM25 content filter for query-based filtering
        content_filter = BM25ContentFilter(user_query=options.query)
        return DefaultMarkdownGenerator(content_filter=content_filter)
    elif options.fit_markdown:
        # Use pruning filter for noise removal
        content_filter = PruningContentFilter(threshold=0.4, threshold_type="fixed")
        return DefaultMarkdownGenerator(content_filter=content_filter)
    return None


def _build_crawler_config(options: CrawlOptions) -> CrawlerRunConfig:
    """Build crawler run configuration."""
    config_kwargs = {
        "cache_mode": _get_cache_mode(options.cache),
        "wait_for": options.wait_for,
        "wait_until": options.wait_until,
        "screenshot": options.screenshot,
        "pdf": options.pdf,
        "page_timeout": options.timeout,
        "remove_overlay_elements": options.remove_overlays,
        "exclude_external_links": options.internal_links_only,
    }

    # Use native scan_full_page for scrolling
    if options.scroll:
        config_kwargs["scan_full_page"] = True
        config_kwargs["scroll_delay"] = options.scroll_delay / 1000.0  # Convert ms to seconds

    # Add custom JS if specified
    if options.execute_js:
        config_kwargs["js_code"] = [options.execute_js]

    # Add extraction strategy if specified
    extraction_strategy = _build_extraction_strategy(options)
    if extraction_strategy:
        config_kwargs["extraction_strategy"] = extraction_strategy

    # Add markdown generator with content filter
    markdown_generator = _build_markdown_generator(options)
    if markdown_generator:
        config_kwargs["markdown_generator"] = markdown_generator

    return CrawlerRunConfig(**config_kwargs)


def _get_markdown_content(result: Any, options: CrawlOptions) -> str:
    """Extract markdown content from result."""
    if hasattr(result, 'markdown'):
        md = result.markdown
        if hasattr(md, 'fit_markdown') and (options.fit_markdown or options.query):
            return md.fit_markdown or md.raw_markdown or ""
        elif hasattr(md, 'raw_markdown'):
            return md.raw_markdown or ""
        elif isinstance(md, str):
            return md
    return ""


def _extract_links(result: Any) -> list[str]:
    """Extract links from crawl result."""
    if hasattr(result, 'links') and result.links:
        if isinstance(result.links, dict):
            return result.links.get("internal", []) + result.links.get("external", [])
        elif isinstance(result.links, list):
            return result.links
    return []


def _extract_media(result: Any) -> list[str]:
    """Extract media URLs from crawl result."""
    if hasattr(result, 'media') and result.media:
        if isinstance(result.media, dict):
            return result.media.get("images", [])
        elif isinstance(result.media, list):
            return result.media
    return []


def _get_content_by_format(result: Any, url: str, options: CrawlOptions) -> str:
    """Get content in the requested format."""
    if options.output_format == "markdown":
        return _get_markdown_content(result, options)
    elif options.output_format == "html":
        return result.html or ""
    elif options.output_format == "cleaned":
        return result.cleaned_html if hasattr(result, 'cleaned_html') else (result.html or "")
    elif options.output_format == "raw":
        return result.raw_html if hasattr(result, 'raw_html') else (result.html or "")
    elif options.output_format == "json":
        title = result.metadata.get("title", "") if hasattr(result, 'metadata') and result.metadata else ""
        return json.dumps({
            "url": url, "title": title, "markdown": _get_markdown_content(result, options),
            "links": _extract_links(result), "images": _extract_media(result),
        }, indent=2)
    return _get_markdown_content(result, options)


def _handle_screenshot(result: Any, url: str, options: CrawlOptions) -> Optional[str]:
    """Save screenshot if requested and available."""
    if not (options.screenshot and hasattr(result, 'screenshot') and result.screenshot):
        return None
    if options.screenshot_path:
        screenshot_path = options.screenshot_path
    else:
        screenshot_path = f"screenshot_{url.replace('://', '_').replace('/', '_')[:50]}.png"
    Path(screenshot_path).write_bytes(result.screenshot)
    return screenshot_path


def _handle_pdf(result: Any, url: str, options: CrawlOptions) -> Optional[str]:
    """Save PDF if requested and available."""
    if not (options.pdf and hasattr(result, 'pdf') and result.pdf):
        return None
    if options.pdf_path:
        pdf_path = options.pdf_path
    else:
        pdf_path = f"page_{url.replace('://', '_').replace('/', '_')[:50]}.pdf"
    Path(pdf_path).write_bytes(result.pdf)
    return pdf_path


async def crawl_url(url: str, options: CrawlOptions) -> CrawlResult:
    """Crawl a single URL."""
    session_manager = SessionManager()
    browser_config = _build_browser_config(options, session_manager)
    crawler_config = _build_crawler_config(options)

    try:
        async with AsyncWebCrawler(config=browser_config) as crawler:
            result = await crawler.arun(url=url, config=crawler_config)

            if not result.success:
                error = result.error_message if hasattr(result, 'error_message') else "Crawl failed"
                return CrawlResult(url=url, success=False, error=error)

            content = _get_content_by_format(result, url, options)
            screenshot_path = _handle_screenshot(result, url, options)
            pdf_path = _handle_pdf(result, url, options)
            links = _extract_links(result) if options.extract_links else []
            media = _extract_media(result) if options.extract_media else []
            status_code = result.status_code if hasattr(result, 'status_code') else None

            if options.session:
                session_manager.update_last_used(options.session)

            extracted = result.extracted_content if hasattr(result, 'extracted_content') and (options.llm_extract or options.schema_path) else None
            metadata = result.metadata if hasattr(result, 'metadata') and result.metadata else {}

            return CrawlResult(
                url=url, success=True, content=content, status_code=status_code,
                links=links, media=media, screenshot_path=screenshot_path,
                pdf_path=pdf_path, extracted_data=extracted, metadata=metadata,
            )

    except (ConnectionError, TimeoutError, OSError) as e:
        return CrawlResult(url=url, success=False, error=str(e))
    except RuntimeError as e:
        # Crawl4AI may raise RuntimeError for browser issues
        return CrawlResult(url=url, success=False, error=str(e))


def crawl(url: str, options: CrawlOptions) -> CrawlResult:
    """Synchronous wrapper for crawl_url."""
    return asyncio.run(crawl_url(url, options))
