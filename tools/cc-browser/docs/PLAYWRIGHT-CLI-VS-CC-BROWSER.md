# When to Use Playwright CLI vs cc-browser

**Date:** 2026-03-08
**Status:** Active Guidance

---

## The Rule

**Use Playwright CLI when the target site has NO bot/human detection.**
**Use cc-browser when the target site has bot detection (LinkedIn, Reddit, etc.).**

Pick the right tool for the job. Using cc-browser where Playwright CLI would work is wasteful.
Using Playwright CLI where cc-browser is needed will get you blocked.

---

## Why Playwright CLI is Better (When You Can Use It)

Playwright CLI (`@playwright/cli`) is a token-efficient CLI for browser automation designed
specifically for coding agents like Claude Code. It was created by Microsoft as a companion
to the Playwright MCP server, but with dramatically lower token usage.

**Advantages over cc-browser:**

| Factor | Playwright CLI | cc-browser |
|--------|---------------|------------|
| Token cost | Very low (accessibility tree saved to disk, only summary sent to LLM) | Higher (screenshots, full page state) |
| Parallel agents | Native support -- run multiple sessions simultaneously | Single connection at a time |
| Headless mode | Default -- no visible browser window | Always headed (real browser profile) |
| Setup per site | None -- just `playwright-cli open <url>` | Requires a named connection with profile |
| Session management | Built-in named sessions, dashboard via `playwright-cli show` | Connection-based with daemon |
| Speed | Fast -- no human-like delays needed | Deliberately slow -- random jitter, human-like typing |

**Playwright CLI is 90,000+ tokens cheaper than Playwright MCP for the same task**, and even
more efficient than screenshot-based approaches like cc-browser.

### Key capabilities

- Full browser control: navigate, click, type, fill, drag, hover, select, upload
- Tabs, cookies, localStorage, sessionStorage management
- Network request mocking and interception
- Screenshots, PDFs, video recording, tracing
- JavaScript evaluation in page context
- Persistent sessions across agent invocations
- Visual monitoring dashboard (`playwright-cli show`)

---

## Why cc-browser is Still Necessary

cc-browser exists because many high-value sites aggressively detect and block automation:

- **LinkedIn** -- detects headless browsers, automation patterns, and non-human timing
- **Reddit** -- flags automated interactions, rate-limits aggressively
- **Banking/financial sites** -- bot detection as a security measure
- **Any site using Cloudflare Bot Management, DataDome, PerimeterX, etc.**

cc-browser defeats detection by:

1. Using real Chrome/Brave browser profiles (not Playwright's bundled Chromium)
2. Running headed with full GPU rendering and browser fingerprinting intact
3. Injecting human-like delays with random jitter on all interactions
4. Maintaining persistent cookies and login state across sessions
5. Appearing identical to a real user browsing manually

Playwright CLI's bundled Chromium is trivially detectable by these systems. Its browser
fingerprint, WebDriver flags, and automation markers will get you blocked immediately.

---

## Decision Matrix

```
Is the site protected by bot detection?
  |
  +-- YES --> Use cc-browser
  |           (LinkedIn, Reddit, Spotify, banking, Cloudflare-protected sites)
  |
  +-- NO  --> Use Playwright CLI
  |           (your own dev server, public docs, demo sites, internal tools,
  |            static sites, APIs with browser UI)
  |
  +-- NOT SURE --> Try Playwright CLI first
                   If you get blocked, CAPTCHAs, or empty responses,
                   switch to cc-browser
```

---

## Common Use Cases by Tool

### Playwright CLI

- **UI testing** -- test form submissions, page flows, edge cases on your own sites
- **Scraping public data** -- documentation sites, public APIs, open data portals
- **Internal tools** -- admin panels, dashboards, staging environments
- **Demo and prototype testing** -- verify UI changes before deployment
- **Parallel testing** -- spawn 3+ agents testing different scenarios simultaneously
- **Screenshot/PDF generation** -- capture pages for reports or documentation

### cc-browser

- **LinkedIn** -- connections, messaging, profile browsing, job searches
- **Reddit** -- posting, commenting, browsing feeds
- **Spotify** -- playlist management, search, playback control
- **Any authenticated site with bot detection** -- where maintaining a real browser
  session is the only way to avoid being blocked

---

## Installation

### Playwright CLI

```bash
npm install -g @playwright/cli@latest
npx playwright install chromium
playwright-cli install --skills
```

### Verify

```bash
playwright-cli --help
```

### Quick test

```bash
playwright-cli open https://example.com --headed
playwright-cli screenshot
playwright-cli close
```

---

## Integration with Claude Code

Playwright CLI installs a Claude Code skill via `playwright-cli install --skills`.
After installation, you can use plain language:

```
Use the playwright CLI to test the login form on http://localhost:3000
```

For repeated workflows, turn the process into a custom skill using the skill creator.
This avoids describing the full workflow each time.

### Parallel sub-agents

Claude Code can spawn multiple sub-agents, each running their own Playwright CLI session:

```
Spin up three parallel sub-agents using the Playwright CLI skill.
Test the form submission on http://localhost:3000 from three angles:
1. Happy path with valid data
2. Edge cases with empty/invalid fields
3. Validation error handling
Make them headed so I can watch.
```

---

## References

- Playwright CLI GitHub: https://github.com/microsoft/playwright-cli
- Playwright MCP GitHub: https://github.com/microsoft/playwright-mcp
- Playwright Docs: https://playwright.dev/docs/test-cli
- cc-browser DEVELOPMENT.md: ./DEVELOPMENT.md
