# cc-websiteaudit

Comprehensive website auditing CLI that analyzes sites across technical SEO, on-page SEO, security, structured data, and AI readiness. Produces grades and detailed reports in multiple formats.

## What It Does

- Crawls websites (breadth-first, configurable depth and page limit)
- Runs 5 analyzer modules with 36 individual checks
- Scores each category 0-100 and assigns letter grades (A+ through F)
- Calculates weighted overall grade
- Identifies quick wins by impact/effort ratio
- Generates reports in console, JSON, HTML, Markdown, and PDF formats
- Auto-detects Cloudflare/SPA sites and switches to browser rendering

## What It Does NOT Do

- Does not modify websites or submit forms
- Does not test performance/load times (use Lighthouse for that)
- Does not check accessibility (WCAG compliance)
- Does not require authentication or API keys
- Does not test mobile rendering

## Installation

Built as part of cc-director suite:

```powershell
.\build.ps1
```

Requires Node.js 18+ and Chrome/Chromium (for PDF output and SPA detection).

## Usage

### Basic audit (console output)

```bash
cc-websiteaudit https://example.com
```

### Save as PDF report

```bash
cc-websiteaudit example.com -o report.pdf
```

### Save as JSON (for cc-brandingrecommendations)

```bash
cc-websiteaudit example.com --format json -o audit.json
```

### Other formats

```bash
cc-websiteaudit example.com --format markdown -o audit.md
cc-websiteaudit example.com --format html -o audit.html
```

### Custom crawl settings

```bash
cc-websiteaudit example.com --pages 50 --depth 4 --verbose
```

### Run specific modules only

```bash
cc-websiteaudit example.com --modules technical-seo,security
```

### Quiet mode (grade only)

```bash
cc-websiteaudit example.com --quiet
```

## Options

| Option | Description | Default |
|--------|-------------|---------|
| `<url>` | Website URL to audit (required) | - |
| `-o, --output <path>` | Output file path (format from extension) | stdout |
| `--format <type>` | console, json, html, markdown, pdf | console |
| `--modules <list>` | Comma-separated modules to run | all |
| `--pages <number>` | Max pages to crawl | 25 |
| `--depth <number>` | Max crawl depth | 3 |
| `--verbose` | Show detailed progress | false |
| `--quiet` | Only show final grade | false |

## Analyzer Modules

### Technical SEO (weight: 20%)

8 checks: robots.txt, XML sitemap, canonicals, HTTPS, redirect chains, status codes, crawl depth, URL structure.

### On-Page SEO (weight: 20%)

8 checks: title tags, meta descriptions, heading hierarchy, image alt text, internal linking, content length, duplicate content, Open Graph.

### Security (weight: 10%)

7 checks: HSTS, CSP, X-Content-Type-Options, X-Frame-Options, Referrer-Policy, Permissions-Policy.

### Structured Data (weight: 10%)

6 checks: JSON-LD presence, Organization schema, Article schema, FAQ schema, Breadcrumb schema, schema validity.

### AI Readiness (weight: 20%)

7 checks: /llms.txt, AI crawler access (GPTBot, ClaudeBot, PerplexityBot, etc.), content citability, passage structure, semantic HTML, entity clarity, question coverage.

## Grading Scale

| Grade | Score Range |
|-------|-------------|
| A+ | 97-100 |
| A  | 93-96 |
| A- | 90-92 |
| B+ | 87-89 |
| B  | 83-86 |
| B- | 80-82 |
| C+ | 77-79 |
| C  | 73-76 |
| C- | 70-72 |
| D+ | 67-69 |
| D  | 63-66 |
| D- | 60-62 |
| F  | 0-59 |

## Check Status Values

- **PASS** - Check passed (score: 100)
- **WARN** - Warning, partial issue (score: 50)
- **FAIL** - Check failed (score: 0)
- **SKIP** - Could not evaluate (not scored)

## Workflow with cc-brandingrecommendations

```bash
# Step 1: Audit the website
cc-websiteaudit example.com --format json -o audit.json

# Step 2: Generate action plan
cc-brandingrecommendations --audit audit.json -o plan.md

# Step 3: Convert to PDF
cc-markdown plan.md -o plan.pdf --theme boardroom
```

## Dependencies

- Node.js 18+
- Chrome/Chromium (for PDF generation and SPA rendering)
- cheerio (HTML parsing)
- commander (CLI)
- puppeteer-core (browser automation)
- undici (HTTP client)

## Testing

```bash
npm test
```
