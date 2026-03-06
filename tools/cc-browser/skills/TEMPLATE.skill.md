---
name: {site-name}
version: YYYY.MM.DD
description: Navigate {Site Name} web interface
site: {domain.com}
flavor: managed
forked_from: null
---

# {Site Name} Navigation Skill

## IMPORTANT: {Safety Warning}
{Bot detection level and required precautions, or "No special precautions needed."}

## Authentication Check
{Selectors/indicators for: logged in, not logged in, session expired}

## URL Patterns
| Page | URL |
|------|-----|
| Home | `https://...` |
{All navigable URL patterns with {variable} placeholders}

## Timing and Delays
{Required delays per action type. For sites with bot detection:}
| Action | Wait after | Why |
|--------|-----------|-----|
{Only include this section if the site has bot detection concerns.}

## Key Elements
{CSS selectors organized by page section. Use tables.}
{Include JavaScript extraction where CSS selectors are unreliable.}

### {Section Name}
| Element | Selector |
|---------|----------|

## Workflows
{Step-by-step recipes for each user action.}

### {Workflow Name}
1. Navigate to `{url}`
2. Wait {time}
3. {action}...

## Gotchas
{Site-specific pitfalls: DOM instability, false positives, modals, edge cases}
