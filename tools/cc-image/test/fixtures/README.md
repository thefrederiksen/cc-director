# Test Fixtures: cc-image

## Overview
Test images for validating OCR text extraction and AI image description features.
Since AI model outputs vary between runs, fixtures include reference outputs that
serve as baselines rather than exact match targets.

## Fixtures
- `ocr_simple.png` - Clean screenshot with clearly readable text for OCR extraction
- `ocr_handwritten.jpg` - Handwritten text sample for OCR edge case testing
- `ocr_multicolumn.png` - Document with multi-column layout to test reading order
- `describe_photo.jpg` - Photograph for image description testing
- `describe_diagram.png` - Technical diagram for structured description testing
- `describe_chart.png` - Chart/graph image for data extraction testing
- `reference_outputs/` - Directory containing expected output baselines:
  - `ocr_simple_expected.txt` - Expected OCR text from ocr_simple.png
  - `ocr_handwritten_expected.txt` - Expected OCR text from handwritten sample
  - `describe_photo_keywords.txt` - Keywords expected in photo description
  - `describe_diagram_keywords.txt` - Keywords expected in diagram description

## Notes
- AI outputs vary between model versions; use keyword matching, not exact comparison
- Reference outputs list key terms and phrases that should appear in results
- Test images should be small (under 500KB each) to keep the repo lightweight
- OCR tests validate text extraction accuracy; description tests validate content coverage
- The default engine is Claude Code CLI -- tests should validate that path
- Avoid images with personal or sensitive content

## Last Validated
Date: 2026-02-26
