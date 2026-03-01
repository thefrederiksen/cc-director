"""Batch processing for cc-crawl4ai."""

import sys
import asyncio
import json
from pathlib import Path
from typing import Optional, Callable, Any
from dataclasses import dataclass, field
from datetime import datetime

# Fix Windows console encoding for Crawl4AI Unicode output
if sys.platform == 'win32':
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    if hasattr(sys.stderr, 'reconfigure'):
        sys.stderr.reconfigure(encoding='utf-8', errors='replace')

from crawl4ai import AsyncWebCrawler, BrowserConfig, CrawlerRunConfig

try:
    from .crawler import (
        CrawlOptions, CrawlResult, _build_browser_config, _build_crawler_config,
        _get_markdown_content, _extract_links, _get_content_by_format,
    )
    from .sessions import SessionManager
except ImportError:
    from crawler import (
        CrawlOptions, CrawlResult, _build_browser_config, _build_crawler_config,
        _get_markdown_content, _extract_links, _get_content_by_format,
    )
    from sessions import SessionManager


@dataclass
class BatchResult:
    """Result of batch crawl operation."""
    total: int
    successful: int
    failed: int
    results: list[CrawlResult] = field(default_factory=list)
    start_time: str = ""
    end_time: str = ""
    duration_seconds: float = 0


def _save_batch_screenshot(
    result: Any, index: int, options: CrawlOptions, output_dir: Optional[Path]
) -> Optional[str]:
    """Save screenshot for batch crawl."""
    if not (options.screenshot and hasattr(result, 'screenshot') and result.screenshot and output_dir):
        return None
    screenshot_path = str(output_dir / f"screenshot_{index:04d}.png")
    Path(screenshot_path).write_bytes(result.screenshot)
    return screenshot_path


def _save_content_to_file(
    content: str, index: int, options: CrawlOptions, output_dir: Optional[Path]
) -> None:
    """Save crawl content to output file."""
    if not output_dir:
        return
    ext = {"markdown": ".md", "json": ".json", "html": ".html", "raw": ".html", "cleaned": ".html"}.get(options.output_format, ".md")
    filename = f"page_{index:04d}{ext}"
    (output_dir / filename).write_text(content, encoding="utf-8")


async def _execute_single_crawl(
    url: str, index: int, browser_config: BrowserConfig,
    crawler_config: CrawlerRunConfig, options: CrawlOptions, output_dir: Optional[Path],
) -> CrawlResult:
    """Execute a single crawl operation."""
    try:
        async with AsyncWebCrawler(config=browser_config) as crawler:
            result = await crawler.arun(url=url, config=crawler_config)

            if not result.success:
                error = result.error_message if hasattr(result, 'error_message') else "Crawl failed"
                return CrawlResult(url=url, success=False, error=error)

            content = _get_content_by_format(result, url, options)
            screenshot_path = _save_batch_screenshot(result, index, options, output_dir)
            links = _extract_links(result) if options.extract_links else []
            metadata = result.metadata if hasattr(result, 'metadata') and result.metadata else {}

            _save_content_to_file(content, index, options, output_dir)

            return CrawlResult(
                url=url, success=True, content=content, links=links,
                screenshot_path=screenshot_path, metadata=metadata,
            )
    except (ConnectionError, TimeoutError, OSError) as e:
        return CrawlResult(url=url, success=False, error=str(e))
    except RuntimeError as e:
        # Crawl4AI may raise RuntimeError for browser issues
        return CrawlResult(url=url, success=False, error=str(e))


async def crawl_batch_async(
    urls: list[str],
    options: CrawlOptions,
    parallel: int = 5,
    output_dir: Optional[Path] = None,
    progress_callback: Optional[Callable[[int, int, str], None]] = None,
) -> BatchResult:
    """Crawl multiple URLs in parallel."""
    session_manager = SessionManager()
    browser_config = _build_browser_config(options, session_manager)

    start_time = datetime.now()
    semaphore = asyncio.Semaphore(parallel)

    async def crawl_one(url: str, index: int) -> CrawlResult:
        async with semaphore:
            crawler_config = _build_crawler_config(options)
            crawl_result = await _execute_single_crawl(
                url, index, browser_config, crawler_config, options, output_dir
            )
            if progress_callback:
                progress_callback(index + 1, len(urls), url)
            return crawl_result

    tasks = [crawl_one(url, i) for i, url in enumerate(urls)]
    results = await asyncio.gather(*tasks)

    end_time = datetime.now()
    duration = (end_time - start_time).total_seconds()

    successful = sum(1 for r in results if r.success)
    failed = len(results) - successful

    if output_dir:
        summary = {
            "total": len(urls), "successful": successful, "failed": failed,
            "duration_seconds": duration,
            "results": [
                {"url": r.url, "success": r.success, "error": r.error,
                 "file": f"page_{i:04d}.md" if r.success else None}
                for i, r in enumerate(results)
            ],
        }
        (output_dir / "batch_summary.json").write_text(json.dumps(summary, indent=2))

    return BatchResult(
        total=len(urls), successful=successful, failed=failed,
        results=list(results), start_time=start_time.isoformat(),
        end_time=end_time.isoformat(), duration_seconds=duration,
    )


def crawl_batch(
    urls: list[str],
    options: CrawlOptions,
    parallel: int = 5,
    output_dir: Optional[Path] = None,
    progress_callback: Optional[Callable[[int, int, str], None]] = None,
) -> BatchResult:
    """Synchronous wrapper for crawl_batch_async."""
    return asyncio.run(
        crawl_batch_async(urls, options, parallel, output_dir, progress_callback)
    )


def load_urls_from_file(file_path: Path) -> list[str]:
    """Load URLs from a file (one per line, ignores empty lines and comments)."""
    urls = []
    with open(file_path) as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#"):
                urls.append(line)
    return urls
