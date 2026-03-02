# cc-brandingrecommendations

Branding and SEO recommendations engine that reads cc-websiteaudit JSON reports and generates prioritized, week-by-week action plans with research-backed recommendations.

## What It Does

- Reads JSON audit reports from cc-websiteaudit
- Analyzes website health across 5 audit categories + 3 cross-cutting areas
- Generates prioritized recommendations using Eisenhower matrix (impact vs effort)
- Creates weekly action plans with time estimates based on budget constraints
- Outputs in multiple formats (console, JSON, markdown)

## What It Does NOT Do

- Does not crawl websites (use cc-websiteaudit for that)
- Does not implement changes - only recommends them
- Does not connect to any APIs or external services
- Does not require authentication or API keys

## Installation

Built as part of cc-director suite:

```powershell
.\build.ps1
```

Requires Node.js 18+.

## Usage

### Basic (console output)

```bash
cc-brandingrecommendations --audit report.json
```

### Save as markdown report

```bash
cc-brandingrecommendations --audit report.json -o plan.md
```

### JSON output for programmatic use

```bash
cc-brandingrecommendations --audit report.json --format json -o plan.json
```

### Full options

```bash
cc-brandingrecommendations --audit report.json \
  --budget high \
  --industry saas \
  --keywords "project management,collaboration" \
  --competitors "asana.com,monday.com" \
  --verbose
```

## Options

| Option | Description | Default |
|--------|-------------|---------|
| `--audit <path>` | Path to cc-websiteaudit JSON report (required) | - |
| `-o, --output <path>` | Output file path (auto-detects format from extension) | stdout |
| `--format <type>` | Output format: console, json, markdown | console |
| `--budget <level>` | Weekly budget: low (5h), medium (10h), high (20h) | medium |
| `--industry <type>` | Industry vertical for tailored recommendations | - |
| `--keywords <list>` | Comma-separated target keywords | - |
| `--competitors <list>` | Comma-separated competitor domains | - |
| `--verbose` | Show detailed progress | false |

## Recommendation Categories

### From Audit (5 categories)

1. **Technical SEO** - robots.txt, sitemaps, canonicals, HTTPS, redirects
2. **On-Page SEO** - title tags, meta descriptions, headings, alt text
3. **Security** - HSTS, CSP, X-Frame-Options, Referrer-Policy
4. **Structured Data** - JSON-LD, Organization/Article/FAQ schemas
5. **AI Readiness** - llms.txt, AI crawler access, content citability

### Cross-Cutting (3 generators)

6. **Content Strategy** - editorial calendar, topic clusters, brand voice
7. **Social Presence** - social profiles, content distribution
8. **Backlinks** - link building strategy

## Priority Classification

Uses Eisenhower matrix:

- **Quick Win** - High impact (>=4), low effort (<=2)
- **Strategic** - High impact (>=4), high effort (>=3)
- **Easy Fill** - Low impact (<=3), low effort (<=2)
- **Deprioritize** - Low impact (<=3), high effort (>=3)

## Weekly Planning

Distributes tasks across 12 weeks in phases:

- Weeks 1-2: Quick Wins
- Weeks 3-4: Foundation (Easy Fills + critical Strategic)
- Weeks 5-8: Strategic items
- Weeks 9-12: Optimization

## Workflow

Run cc-websiteaudit first, then feed its JSON output:

```bash
cc-websiteaudit example.com --format json -o audit.json
cc-brandingrecommendations --audit audit.json -o plan.md
cc-markdown plan.md -o plan.pdf --theme boardroom
```

## Dependencies

- Node.js 18+
- commander (CLI parsing)
- cc-websiteaudit JSON output (input)

## Testing

```bash
npm test
```

Tests cover audit parsing, scoring/priority classification, and weekly planning logic.
