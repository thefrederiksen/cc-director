# Test Fixtures: cc-powerpoint

## Overview
Markdown input files and expected output format descriptions for testing
markdown-to-PPTX conversion.

## Fixtures
- `basic_slides.md` - Simple markdown with headings as slide breaks
- `bullet_points.md` - Slides with nested bullet point lists
- `code_blocks.md` - Markdown with fenced code blocks for code slide testing
- `images.md` - Markdown referencing images for media slide testing
- `tables.md` - Markdown with tables for table slide rendering
- `mixed_content.md` - Full presentation with headings, bullets, code, and images
- `empty.md` - Empty markdown file for error handling
- `test_images/` - Directory with small images referenced by images.md:
  - `diagram.png` - Simple diagram for slide embedding
  - `chart.png` - Chart image for slide embedding
- `expected_outputs/` - Directory containing reference output descriptions:
  - `basic_slides_structure.json` - Expected slide count, titles, and layout types
  - `mixed_content_structure.json` - Full expected presentation structure

## Notes
- Slide breaks are defined by top-level headings (# Heading)
- Sub-headings within a slide should be rendered as styled text, not new slides
- Bullet point nesting should be preserved in PPTX output
- Code blocks should use monospace font and optional syntax highlighting
- Image references must use relative paths within the fixtures directory
- PPTX output cannot be compared byte-for-byte; validate structure (slide count,
  titles, content types) using python-pptx or similar library
- Empty markdown should produce either an empty presentation or a clear error
- Test images should be minimal size PNG files (under 50KB each)

## Last Validated
Date: 2026-02-26
