# Test Fixtures: cc-photos

## Overview
Test images for validating photo organization features including duplicate
detection and AI-powered image analysis.

## Fixtures
- `photo_a.jpg` - Original test photo for duplicate detection baseline
- `photo_a_duplicate.jpg` - Exact copy of photo_a.jpg (byte-identical)
- `photo_a_resized.jpg` - Same image as photo_a.jpg but resized (perceptual duplicate)
- `photo_a_cropped.jpg` - Cropped version of photo_a.jpg (near-duplicate test)
- `photo_b.jpg` - Completely different photo (should not match photo_a)
- `photo_no_exif.jpg` - Photo with EXIF metadata stripped
- `photo_with_exif.jpg` - Photo with complete EXIF data (GPS, camera, date)
- `photo_corrupt.jpg` - Intentionally truncated JPEG for error handling
- `expected_outputs/` - Directory containing reference analysis results:
  - `photo_with_exif_metadata.json` - Expected parsed EXIF fields
  - `duplicate_pairs.json` - Expected duplicate detection results

## Notes
- Test images should be small (under 200KB each) to keep the repo lightweight
- Generate test images programmatically if needed (e.g., with ImageMagick or PIL)
- Duplicate detection should find exact copies and perceptual matches
- Resized and cropped variants test perceptual hashing tolerance thresholds
- AI analysis outputs vary; validate keyword presence, not exact descriptions
- EXIF test validates parsing of GPS coordinates, camera model, and date taken
- Corrupt file test should produce a clear error, not a crash
- Do not use photos of real people in test fixtures

## Last Validated
Date: 2026-02-26
