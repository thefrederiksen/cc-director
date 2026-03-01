# Test Fixtures: cc-crawl4ai

## Overview
Local HTML files for offline testing of web crawling and content extraction.
Fixtures simulate various webpage structures without requiring network access.

## Fixtures
- `simple_page.html` - Basic HTML page with headings, paragraphs, and links
- `complex_layout.html` - Page with navigation, sidebar, footer, and main content
- `javascript_rendered.html` - Page with content injected via inline JavaScript
- `table_heavy.html` - Page with data tables for structured extraction testing
- `nested_links.html` - Page with deeply nested anchor tags and relative URLs
- `malformed.html` - Intentionally broken HTML for error tolerance testing
- `empty_page.html` - Valid HTML with no meaningful content (empty body)
- `expected_outputs/` - Directory containing reference extraction results:
  - `simple_page_expected.md` - Expected markdown output from simple_page.html
  - `complex_layout_expected.md` - Expected content extraction (no nav/footer)
  - `table_heavy_expected.md` - Expected table-to-markdown conversion

## Notes
- All HTML files should be self-contained (no external CSS/JS dependencies)
- Tests should point the crawler at local file:// URLs or a local test server
- JavaScript-rendered content tests may require a headless browser backend
- Malformed HTML test validates graceful degradation, not specific output
- Expected outputs focus on content extraction quality (stripping boilerplate)
- Keep fixtures under 100KB each for fast test execution
- Link extraction tests should validate both absolute and relative URL handling

## Last Validated
Date: 2026-02-26
