# Test Fixtures: cc-websiteaudit

## Overview

These fixtures document the expected input/output formats for the cc-websiteaudit tool.
Since the tool requires live HTTP access to audit websites, fixtures focus on schema
validation and format verification rather than exact output matching.

## Fixtures

### input/sample-page.html
- **Purpose**: Reference HTML page with good SEO practices
- **Tests**: Demonstrates what a well-structured page looks like for auditing
- **Contains**: Title, meta description, canonical, Open Graph, JSON-LD Organization schema,
  proper heading hierarchy, image alt text, semantic HTML, internal links

### expected/audit_output_schema.json
- **Purpose**: Documents the complete JSON output schema
- **Tests**: Validates that audit JSON output contains all required fields
- **Contains**: Required top-level fields, category schema, check schema, all expected
  category names, all expected check IDs per category, grade scale mapping

### expected/console_output_format.txt
- **Purpose**: Documents the expected console output format
- **Tests**: Validates console reporter output structure
- **Contains**: Line-by-line format description with status indicator legend

## Running Tests

```bash
# Live audit (requires internet)
cc-websiteaudit https://example.com --format json -o test/fixtures/output/example-audit.json

# Validate output against schema
# Compare output JSON keys against expected/audit_output_schema.json
```

## Validation Approach

Since website audits produce dynamic results based on live site state:
1. Validate JSON structure matches schema (all required fields present)
2. Validate all 5 categories are present
3. Validate each category contains expected check IDs
4. Validate grade is within valid range (A+ through F)
5. Validate scores are 0-100
6. Do NOT compare specific check statuses (sites change over time)

## Updating Expected Output

When new analyzers or checks are added:
1. Update expected/audit_output_schema.json with new check IDs
2. Update this README with new check descriptions
3. Run against a test site and verify the output includes new checks

## Last Validated
Date: 2026-02-26
