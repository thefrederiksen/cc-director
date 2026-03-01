# cc-personresearch

Person research CLI tool - gathers publicly available information about a person from multiple OSINT sources.

## Overview

Given a person's name and email address, cc-personresearch queries dozens of free public data sources and compiles all findings into a structured JSON report. The tool acts as a raw data collector - it does not attempt to disambiguate results. Claude Code (or the user) reviews the output to determine which results belong to the target person.

## Requirements

- Python 3.10+
- cc-browser daemon running (for browser-based sources)
- cc-linkedin installed (for LinkedIn lookups)

## Quick Start

```bash
# Full search (API + browser + LinkedIn)
cc-personresearch search --name "John Smith" --email "john.smith@acme.com"

# Save to file
cc-personresearch search -n "John Smith" -e "john@acme.com" -o report.json

# API sources only (fast, no browser needed)
cc-personresearch search -n "John Smith" -e "john@acme.com" --api-only

# List all available sources
cc-personresearch sources
```

## Commands

### search

Search for a person across all data sources.

| Option | Description |
|--------|-------------|
| `--name, -n` | Person's full name (required) |
| `--email, -e` | Email address |
| `--location, -l` | Location hint |
| `--output, -o` | Output JSON file path |
| `--workspace, -w` | cc-browser workspace (default: chrome-work) |
| `--api-only` | Skip browser sources, only run API lookups |
| `--skip` | Comma-separated source names to skip |
| `--verbose, -v` | Show detailed progress |

### sources

List all available data sources.

## Data Sources

### Phase 1: API Sources (parallel, fast)

| Source | Input | Returns |
|--------|-------|---------|
| gravatar | email | Avatar, display name, linked accounts |
| github | email + name | User profiles, repos, commits |
| fec | name | Campaign donations, employer, address |
| sec_edgar | name | SEC filings, officer positions |
| wayback | email domain | Archived team/about pages |
| whois | email domain | Domain registration info |

### Phase 2: Google Dorking (browser)

Targeted Google searches combining name + email + site-specific queries.

### Phase 3: People-Search Sites (browser)

| Source | Returns |
|--------|---------|
| thatsthem | Phone, address, job title, income |
| truepeoplesearch | Phone, address, age, relatives, emails |
| zabasearch | Phone numbers, addresses |
| nuwber | Phone, address, age, relatives |

### Phase 4: Professional Sources (browser)

| Source | Returns |
|--------|---------|
| company_website | Bio, title from company team page |
| opencorporates | Corporate officer/director records |

### Phase 5: LinkedIn (via cc-linkedin)

| Source | Returns |
|--------|---------|
| linkedin | Profile, headline, location, connections |

## Output Format

JSON report with:
- `search_params` - input parameters
- `summary` - counts of sources queried, found, failed
- `sources` - per-source results with status and data
- `discovered_urls` - all URLs found across sources

## Examples

```bash
# Quick API-only check
cc-personresearch search -n "Jane Doe" -e "jane@company.com" --api-only -v

# Full research with output file
cc-personresearch search -n "Jane Doe" -e "jane@example.com" -o results.json -v

# Skip specific sources
cc-personresearch search -n "John Smith" --skip fec,sec_edgar

# Use specific browser workspace
cc-personresearch search -n "John Smith" -w chrome-personal
```

## Build

```powershell
cd src/cc-personresearch
powershell -ExecutionPolicy Bypass -File build.ps1
```
