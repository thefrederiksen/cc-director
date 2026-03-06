# cc-browser Wishlist: Features, Gaps, and Dream Improvements

**Date:** 2026-03-06
**Status:** Research Complete - Prioritization Pending

---

## Executive Summary

cc-browser v2 uses a Chrome Extension + Native Messaging architecture that is fundamentally undetectable by anti-bot systems. This is a major advantage over Playwright/Puppeteer/Selenium-based tools. However, the extension-only architecture introduces limitations (no headless, 1MB message limit, no response body interception) that competing tools handle natively.

This document catalogs every improvement opportunity discovered through:
- Deep analysis of cc-browser's current capabilities
- Research into 15+ competing browser automation tools
- Analysis of emerging AI-powered browser automation patterns

---

## Table of Contents

1. [Speed and Performance](#1-speed-and-performance)
2. [Headless and Background Execution](#2-headless-and-background-execution)
3. [Recording, Replay, and SOPs](#3-recording-replay-and-sops)
4. [Interactive / Human-in-the-Loop Mode](#4-interactive--human-in-the-loop-mode)
5. [AI-Powered Intelligence](#5-ai-powered-intelligence)
6. [Self-Healing and Adaptive Selectors](#6-self-healing-and-adaptive-selectors)
7. [Workflow Engine and Chaining](#7-workflow-engine-and-chaining)
8. [Network and Data Interception](#8-network-and-data-interception)
9. [Structured Data Extraction](#9-structured-data-extraction)
10. [Screenshot and Visual Intelligence](#10-screenshot-and-visual-intelligence)
11. [File and Download Management](#11-file-and-download-management)
12. [PDF Generation](#12-pdf-generation)
13. [Multi-Tab and Parallel Execution](#13-multi-tab-and-parallel-execution)
14. [Cookie and Session Management](#14-cookie-and-session-management)
15. [CAPTCHA and Anti-Bot Handling](#15-captcha-and-anti-bot-handling)
16. [MCP Protocol Integration](#16-mcp-protocol-integration)
17. [Accessibility and ARIA Improvements](#17-accessibility-and-aria-improvements)
18. [Element Interaction Improvements](#18-element-interaction-improvements)
19. [Wait and Timing Intelligence](#19-wait-and-timing-intelligence)
20. [Navigation and Browsing](#20-navigation-and-browsing)
21. [Developer Experience](#21-developer-experience)
22. [Monitoring and Debugging](#22-monitoring-and-debugging)
23. [Cloud and Scaling](#23-cloud-and-scaling)
24. [Skill System Improvements](#24-skill-system-improvements)
25. [Security and Privacy](#25-security-and-privacy)

---

## 1. Speed and Performance

### Current State
- Commands go through: CLI -> HTTP -> Daemon -> WebSocket -> Native Host -> Extension -> Content Script -> DOM. That is 7 hops per action.
- Each command is a separate HTTP request with full JSON round-trip.
- No batching, no pipelining, no command streaming.

### Wishlist

**1.1 Command Batching**
Send multiple commands in a single request and get all results back at once. Example: navigate + wait + snapshot in one call instead of three round-trips.
- Inspired by: Playwright's `Promise.all()` pattern, Stagehand v3's batched operations.

**1.2 Command Pipelining**
Stream commands over a persistent connection (WebSocket from CLI to daemon) instead of individual HTTP requests. The CLI could maintain a WebSocket connection for the duration of a session.
- Estimated speedup: 30-50% for multi-step workflows.

**1.3 Lazy Snapshot Loading**
Only compute the full ARIA tree when requested. Currently, every snapshot walks the entire DOM. Allow partial snapshots (e.g., only within a CSS selector scope) to reduce computation on large pages.
- Current: `cc-browser snapshot` always scans entire page.
- Wanted: `cc-browser snapshot --selector ".main-content"` (already supported but could be optimized further).

**1.4 Element Reference Caching**
Cache element references across commands within the same page state. Currently, refs are invalidated after every navigation or DOM change. Allow "soft" refs that attempt to re-resolve before failing.
- Inspired by: Stagehand v3's automatic element caching that reuses without additional LLM inference.

**1.5 Parallel Content Script Execution**
For multi-tab operations, execute content script commands in parallel across tabs instead of sequentially.

---

## 2. Headless and Background Execution

### Current State
- Chrome extensions do NOT work in headless mode. This is a fundamental Chromium limitation.
- Every connection requires a visible browser window.
- This is actually a feature for anti-detection (headless is a bot signal), but limits batch/background use cases.

### Wishlist

**2.1 Virtual Display Mode (Linux/CI)**
On Linux, use Xvfb (X Virtual Framebuffer) to run a "headed" browser without a physical display. The browser thinks it has a screen, extensions work, but no window is visible. This is how CI systems run headed browsers.
- Windows equivalent: Use a virtual desktop or minimized window.

**2.2 Minimized Window Mode**
Launch Chrome with `--start-minimized` or use `chrome.windows.update({ state: 'minimized' })` after launch. The browser runs in background, extensions still work, but the window doesn't compete for screen space.
- Easy win -- just add a `--background` flag to `connections open`.

**2.3 Offscreen Document API**
Chrome's Offscreen Documents API (MV3) allows creating off-screen pages that can run DOM operations. Investigate if this can handle some automation tasks without a visible tab.
- Limitation: Cannot interact with real web pages, only extension-created documents.

**2.4 Headless with CDP Fallback**
For tasks where detection doesn't matter (internal tools, localhost, testing), offer a `--headless` flag that switches to CDP-based automation (Playwright). This would be a separate execution mode, not the default.
- Use case: CI pipelines, internal tool automation, testing.

---

## 3. Recording, Replay, and SOPs

### Current State
- The skill system provides markdown instruction files that Claude reads.
- No recording capability -- all instructions must be written manually.
- No replay engine -- skills are interpreted by Claude, not executed deterministically.
- No SOP import feature.

### Wishlist

**3.1 Action Recording**
Record user actions in the browser and generate a replayable script. The content script watches for clicks, types, navigations, and scrolls, then saves a structured action log.
- Output format: JSON action sequence or markdown skill instructions.
- Inspired by: Playwright codegen, Axiom.ai's record mode, Macro Cursor Recorder.
- Implementation: Content script adds event listeners for click/input/navigation, sends events to daemon which writes them to a file.

**3.2 Deterministic Replay Engine**
Replay recorded action sequences without AI interpretation. Fast, cheap (no LLM calls), and repeatable. Use element selectors, text content matching, and ARIA refs for element resolution.
- Command: `cc-browser replay --file workflow.json --connection linkedin`
- Supports: wait conditions, variable substitution, conditional branching.

**3.3 SOP Import and Execution**
Accept a markdown SOP document (human-written instructions) and convert it to an executable workflow. Uses LLM to interpret steps, maps them to cc-browser commands.
- Inspired by: Skyvern's SOP Upload feature.
- Command: `cc-browser sop run --file linkedin-post.md --connection linkedin`
- The SOP file would be a markdown document with numbered steps in plain English.

**3.4 Hybrid Record + Edit**
Record a rough workflow, then edit the generated steps to add conditions, loops, and error handling. Export as a reusable skill or JSON workflow.

**3.5 Workflow Variables and Templates**
Support variables in recorded workflows: `{{email}}`, `{{password}}`, `{{message}}`. Values provided at runtime or from a data file.
- Use case: Run the same workflow for 50 different contacts with different messages.
- Command: `cc-browser replay --file send-message.json --data contacts.csv`

**3.6 Action History / Audit Log**
Record every command executed against a connection with timestamps. Useful for debugging, replaying, and learning.
- Automatically saved to `%LOCALAPPDATA%\cc-director\connections\<name>\history.jsonl`

---

## 4. Interactive / Human-in-the-Loop Mode

### Current State
- CLI is command-by-command. No interactive shell.
- No way for the tool to pause and ask the user for guidance.
- No live view of what the browser is doing (except looking at the actual browser window).

### Wishlist

**4.1 Interactive Shell (REPL)**
An interactive mode where you type commands and see results in real-time, with tab completion and command history.
- Command: `cc-browser interactive --connection linkedin`
- Features: auto-snapshot after each action, show current URL, element count, tab info in prompt.

**4.2 Pause and Resume**
During automated workflows, allow pausing for human intervention (CAPTCHA, 2FA, unexpected state), then resume automation.
- Inspired by: Amazon Bedrock AgentCore's pause-for-human pattern, Browserless hybrid automation.
- The daemon sends a "paused" event, CLI shows "Waiting for human action... Press Enter to continue."

**4.3 Step-by-Step Mode**
Execute a workflow one step at a time, showing the user what will happen next and waiting for confirmation before each step.
- Command: `cc-browser replay --file workflow.json --step-by-step`
- Shows: "Next: Click 'Submit' button [ref=e5]. Press Enter to execute, 's' to skip, 'q' to quit."

**4.4 Live Session Sharing**
Generate a read-only view URL that shows what the browser is doing in real-time (screenshots streamed at intervals).
- Use case: Monitor automation running on a remote machine.
- Implementation: Daemon serves a web page with periodic screenshot updates.

**4.5 Voice-Guided Automation**
Speak commands to the browser: "Click the login button", "Type my email address", "Scroll down".
- Integration with speech-to-text, then natural language -> cc-browser command mapping.

---

## 5. AI-Powered Intelligence

### Current State
- Claude interprets skill files and decides what cc-browser commands to run.
- No built-in AI -- the intelligence comes from the LLM calling cc-browser as a tool.
- Element resolution is purely structural (ARIA tree refs, CSS selectors, text matching).

### Wishlist

**5.1 Natural Language Commands**
Execute browser actions with plain English: `cc-browser do "Log into LinkedIn and go to my messages"`
- The daemon sends the instruction to an LLM along with the current page snapshot.
- The LLM generates a sequence of cc-browser commands.
- Commands are executed and results fed back to the LLM for next steps.
- Inspired by: Stagehand's act/extract/observe, Browser Use's natural language interface.

**5.2 Smart Element Resolution**
When a ref or selector fails, use AI to find the most likely matching element based on context.
- Example: `cc-browser click --text "Submit"` fails because the button says "Send". AI recognizes "Submit" and "Send" are semantically similar and clicks the right element.
- Inspired by: Self-healing locators in Testim, BrowserStack's AI-driven stability.

**5.3 Page Understanding / extract()**
Extract structured data from a page using natural language: `cc-browser extract "Get all job titles and companies from this LinkedIn search results page"`
- Returns JSON with the extracted data.
- Inspired by: Stagehand's extract() primitive, Browser Use's data extraction.

**5.4 observe() -- Page State Description**
Describe what's on the current page and what actions are available: `cc-browser observe --connection linkedin`
- Returns: "You are on the LinkedIn Feed page. Available actions: create a post, view messages (8 unread), view notifications, search, view profile."
- Inspired by: Stagehand's observe() primitive.

**5.5 Goal-Oriented Automation**
Give the tool a high-level goal and let it figure out the steps: `cc-browser goal "Find and connect with 5 people who work at Microsoft in Seattle"`
- The AI plans the workflow, executes it, handles errors, and reports results.
- This is the highest level of abstraction -- full agent mode.

**5.6 Visual Page Understanding**
Use screenshot + vision model to understand the page when the DOM/ARIA tree is insufficient (canvas elements, complex visualizations, image-heavy pages).
- Take a screenshot, send to vision model, get structured description.
- Fallback when ARIA snapshot returns insufficient information.

---

## 6. Self-Healing and Adaptive Selectors

### Current State
- Element refs are computed fresh for each snapshot (e1, e2, e3...).
- Refs change between snapshots even for the same element.
- No memory of what elements were interacted with previously.
- If a selector or ref fails, the command fails.

### Wishlist

**6.1 Stable Element IDs**
Assign stable IDs to elements based on their attributes (id, aria-label, role, text content, position in tree) rather than sequential numbers. Same element gets the same ID across snapshots.
- Example: A "Submit" button is always `btn-submit` instead of `e47` in one snapshot and `e52` in the next.

**6.2 Multi-Attribute Element Signatures**
Store a fingerprint for each element: { role, name, text, cssSelector, xpath, position, parentChain }. When resolving, try all attributes and use the best match.
- If the primary locator fails, fall back to alternative attributes.
- Log which fallback was used so the skill can be updated.

**6.3 Learned Element Patterns**
After successfully interacting with an element, save its signature to the connection's learned patterns. On future visits, use the learned signature for faster, more reliable resolution.
- Current: `cc-browser skills learn` stores text patterns.
- Wanted: Automatic learning of element signatures after successful interactions.

**6.4 DOM Change Detection**
Detect when the DOM has changed since the last snapshot and notify the caller. Prevents using stale refs.
- MutationObserver in the content script watches for significant DOM changes.
- Commands that use refs check if the DOM has changed and auto-refresh if needed.

---

## 7. Workflow Engine and Chaining

### Current State
- Each command is independent. No built-in flow control.
- Workflows are orchestrated by the calling tool (Claude, cc-reddit, etc.).
- No loops, conditions, or error recovery at the cc-browser level.

### Wishlist

**7.1 Workflow Definition Language**
Define multi-step workflows in YAML or JSON with conditions, loops, and error handling:

```yaml
name: linkedin-send-message
connection: linkedin
steps:
  - navigate: https://linkedin.com/messaging
  - wait: { text: "Messaging" }
  - click: { text: "{{recipient}}" }
  - type: { selector: ".msg-form__contenteditable", text: "{{message}}" }
  - click: { text: "Send" }
  - wait: { text: "Message sent", timeout: 5000 }
on_error:
  - screenshot: { save: "./error-{{timestamp}}.jpg" }
  - notify: "Message send failed for {{recipient}}"
```

**7.2 Conditional Branching**
If/else based on page state: "If the login button is visible, log in first. Otherwise, proceed to the feed."

**7.3 Loop Constructs**
Repeat actions for a list of items: "For each contact in contacts.csv, send a personalized message."

**7.4 Error Recovery Strategies**
Define what to do when a step fails: retry N times, skip and continue, take screenshot, notify user, or abort.

**7.5 Workflow Composition**
Import and call other workflows as sub-routines. A "send-message" workflow calls a "login-if-needed" workflow.

**7.6 Scheduled Execution**
Run workflows on a schedule: "Every morning at 9am, check LinkedIn messages."
- Integration with system scheduler (cron/Task Scheduler) or built-in scheduling.

---

## 8. Network and Data Interception

### Current State
- No network interception capability.
- `chrome.webRequest` can observe URLs/headers/status but NOT response bodies.
- Cannot mock requests, block resources, or capture API responses.

### Wishlist

**8.1 Request/Response Logging**
Log all network requests and responses for a connection. Useful for debugging and understanding site behavior.
- Command: `cc-browser network --connection linkedin --log`
- Uses `chrome.webRequest.onCompleted` for metadata.

**8.2 Request Blocking / Filtering**
Block specific requests (ads, analytics, tracking pixels) to speed up page loads and reduce noise.
- Command: `cc-browser network block "*.google-analytics.com/*"`
- Uses `chrome.declarativeNetRequest` (MV3 compatible).

**8.3 Response Body Capture via Fetch Override**
Inject a content script that overrides `fetch()` and `XMLHttpRequest` to capture response bodies. This works around the `chrome.webRequest` limitation.
- Useful for capturing API responses (e.g., LinkedIn's internal API data).
- Opt-in per connection: `cc-browser network capture --connection linkedin --pattern "*/api/*"`

**8.4 Request Mocking**
Serve fake responses for specific URLs. Useful for testing and development.
- Command: `cc-browser network mock "*/api/users" --response '{"users": []}'`

**8.5 Bandwidth Throttling**
Simulate slow network connections for testing.
- Uses `chrome.debugger` API with Network.emulateNetworkConditions.

---

## 9. Structured Data Extraction

### Current State
- `cc-browser snapshot` returns ARIA tree text.
- `cc-browser text` returns raw text content.
- `cc-browser html` returns raw HTML.
- `cc-browser evaluate` can run arbitrary JS.
- No structured data extraction (tables, lists, forms, etc.).

### Wishlist

**9.1 Table Extraction**
Automatically detect and extract HTML tables as structured JSON or CSV.
- Command: `cc-browser extract-table --selector "table.results" --connection linkedin`
- Returns: `[{"Name": "John", "Title": "Engineer"}, ...]`

**9.2 List Extraction**
Extract repeating patterns (search results, feed items, contact lists) as structured arrays.
- Command: `cc-browser extract-list --selector ".search-result" --fields "name:.name,title:.title,url:a@href"`

**9.3 Form State Extraction**
Read all form fields, their current values, and validation state.
- Command: `cc-browser extract-form --selector "form#login"`
- Returns: `[{"name": "email", "type": "text", "value": "", "required": true}, ...]`

**9.4 AI-Powered Extraction (see 5.3)**
Use natural language to describe what data you want extracted, and let AI figure out the selectors.

**9.5 Pagination and Infinite Scroll Handling**
Automatically click "Next" or scroll to load all results, extracting data from each page.
- Command: `cc-browser extract-all --selector ".result" --pagination ".next-button" --max-pages 10`
- For infinite scroll: `--scroll-to-end --wait-between 2000`

---

## 10. Screenshot and Visual Intelligence

### Current State
- `cc-browser screenshot` captures visible viewport as base64 (JPEG/PNG).
- No full-page screenshot (limited by `chrome.tabs.captureVisibleTab`).
- No element-level screenshot.
- No screenshot comparison or visual diffing.
- No save-to-file option from CLI.

### Wishlist

**10.1 Save to File**
Save screenshot directly to a file path.
- Command: `cc-browser screenshot --save ./page.png --connection linkedin`
- Already planned in FEATURE_PLAN.md. Just needs implementation.

**10.2 Full-Page Screenshot**
Capture the entire scrollable page, not just the visible viewport.
- Implementation: Scroll-and-stitch in content script. Capture viewport, scroll down, capture again, stitch together.
- Command: `cc-browser screenshot --full-page --save ./full.png`

**10.3 Element Screenshot**
Capture a specific element by ref or selector.
- Implementation: Get element bounding rect, capture viewport, crop.
- Command: `cc-browser screenshot --ref e5 --save ./element.png`

**10.4 Visual Regression / Comparison**
Compare two screenshots and highlight differences.
- Command: `cc-browser screenshot diff --baseline ./before.png --current ./after.png --output ./diff.png`
- Returns: percentage changed, regions affected.

**10.5 Annotated Screenshots**
Overlay element refs, labels, or highlights on the screenshot.
- Command: `cc-browser screenshot --annotate --save ./annotated.png`
- Draws boxes around interactive elements with their ref IDs.
- Useful for debugging and documentation.

**10.6 Video Recording**
Record a sequence of screenshots as a video/GIF during a workflow.
- Command: `cc-browser record start --connection linkedin` / `cc-browser record stop --save ./session.mp4`
- Captures screenshots at intervals (e.g., every 500ms) and stitches into video.

**10.7 Vision Model Integration**
Send screenshot to a vision model for page understanding when ARIA tree is insufficient.
- Command: `cc-browser vision --connection linkedin --question "What products are shown on this page?"`
- Uses Claude's vision capabilities or similar.

---

## 11. File and Download Management

### Current State
- `cc-browser upload` can upload files via content script (DataTransfer + change event).
- No download management.
- No file picker automation.

### Wishlist

**11.1 Download Tracking**
Track downloads triggered by browser actions. Know when a download starts, its progress, and when it completes.
- Uses `chrome.downloads` API.
- Command: `cc-browser downloads --connection linkedin`

**11.2 Download to Specified Path**
Automatically save downloads to a specified directory.
- Command: `cc-browser download --save-to ./exports/ --connection linkedin`

**11.3 Download Interception**
Intercept download URLs and handle them programmatically (e.g., fetch via Node.js instead of browser).

**11.4 File Picker Automation**
Handle native OS file picker dialogs that can't be automated via DOM.
- This is inherently difficult with extension-only architecture.
- Possible workaround: Pre-set download directory via Chrome preferences.

---

## 12. PDF Generation

### Current State
- No PDF generation capability.
- Playwright can generate PDFs from pages (`page.pdf()`).
- Chrome extensions cannot access the print/PDF API directly.

### Wishlist

**12.1 Page to PDF**
Convert the current page to a PDF file.
- Implementation: Use `chrome.debugger` API with `Page.printToPDF` command.
- Command: `cc-browser pdf --save ./page.pdf --connection linkedin`
- Note: `chrome.debugger` shows "is being debugged" bar -- acceptable for PDF generation.

**12.2 HTML to PDF**
Convert arbitrary HTML to PDF (for report generation).
- Open a data: URL or local file with the HTML content, then convert to PDF.

---

## 13. Multi-Tab and Parallel Execution

### Current State
- Full tab management: list, open, close, focus.
- Commands target a specific tab via `--tab` flag.
- Sequential execution only -- one command at a time per connection.

### Wishlist

**13.1 Parallel Tab Operations**
Execute commands on multiple tabs simultaneously within the same connection.
- Command: `cc-browser parallel --tabs 123,456 snapshot` (snapshot both tabs at once).

**13.2 Tab Pooling**
Open N tabs and process a queue of URLs across them. Like a browser-level thread pool.
- Command: `cc-browser pool --tabs 5 --urls urls.txt --action "snapshot --save ./out/{{index}}.json"`

**13.3 Cross-Connection Orchestration**
Coordinate actions across multiple connections (e.g., copy data from LinkedIn to a CRM).
- Command: `cc-browser orchestrate --workflow cross-platform.yaml`

**13.4 Background Tab Processing**
Process tabs in the background while the user continues working in the foreground tab.
- Content scripts already run in background tabs, but some chrome APIs require the tab to be active.

---

## 14. Cookie and Session Management

### Current State
- Cookies persist in the Chrome profile directory (per-connection isolation).
- No programmatic cookie access from cc-browser.
- Session restore files are deleted to prevent tab accumulation.

### Wishlist

**14.1 Cookie CRUD**
Read, write, and delete cookies programmatically.
- Command: `cc-browser cookies list --connection linkedin --domain .linkedin.com`
- Command: `cc-browser cookies set --name "session_id" --value "abc" --domain ".linkedin.com"`
- Uses `chrome.cookies` API (already in manifest permissions).

**14.2 Cookie Export/Import**
Export all cookies to a file and import them later. Useful for sharing sessions or backup.
- Command: `cc-browser cookies export --connection linkedin --file ./cookies.json`
- Command: `cc-browser cookies import --connection linkedin --file ./cookies.json`

**14.3 Session Snapshot and Restore**
Save the entire browser state (cookies, localStorage, sessionStorage, IndexedDB) and restore it later.
- More comprehensive than just cookies.
- Useful for: cloning a logged-in session, disaster recovery.

**14.4 Auth State Monitoring**
Detect when authentication expires (401 responses, redirect to login page) and notify the user or trigger re-authentication.

---

## 15. CAPTCHA and Anti-Bot Handling

### Current State
- Extension architecture is inherently undetectable (no CDP, no automation flags).
- No CAPTCHA solving capability.
- If a CAPTCHA appears, automation is stuck until human intervenes.

### Wishlist

**15.1 CAPTCHA Detection**
Detect when a CAPTCHA appears on the page (hCaptcha, reCAPTCHA, Cloudflare Turnstile).
- Content script watches for known CAPTCHA element signatures.
- Sends event to daemon: "CAPTCHA detected on linkedin connection."

**15.2 Human-Pause for CAPTCHA**
When CAPTCHA is detected, pause automation and notify the user to solve it manually.
- CLI shows: "CAPTCHA detected. Please solve it in the browser window. Press Enter when done."
- Inspired by: Amazon Bedrock AgentCore's pause-for-human pattern.

**15.3 CAPTCHA Solving Service Integration**
Integrate with third-party CAPTCHA solving services (2Captcha, Anti-Captcha) for automated solving.
- Optional, configurable per connection.
- Sends CAPTCHA image/token to service, waits for solution, injects it.

**15.4 Browser Fingerprint Hardening**
Ensure the browser fingerprint matches a real user profile. Verify: canvas hash, WebGL renderer, fonts, plugins, timezone, language, screen resolution all match consistently.
- Command: `cc-browser fingerprint check --connection linkedin`
- Reports any anomalies that might trigger detection.

---

## 16. MCP Protocol Integration

### Current State
- cc-browser is a CLI tool called via shell commands.
- No MCP (Model Context Protocol) server.
- Claude Code calls cc-browser via bash commands.

### Wishlist

**16.1 MCP Server Mode**
Run cc-browser as an MCP server that any MCP-compatible client can connect to.
- Any AI tool (Claude, Cursor, Windsurf, VS Code) can use cc-browser as a browser automation provider.
- Inspired by: BrowserMCP, Browserbase MCP server, Playwright MCP.

**16.2 MCP Tool Definitions**
Expose all cc-browser commands as MCP tools with proper schemas, descriptions, and parameter types.
- Tools: navigate, snapshot, click, type, screenshot, extract, etc.
- Each tool has a JSON schema for parameters and return types.

**16.3 MCP Resource Exposure**
Expose browser state as MCP resources: current URL, page title, tabs list, cookies, ARIA snapshot.
- Resources update in real-time as the browser state changes.

---

## 17. Accessibility and ARIA Improvements

### Current State
- Full ARIA snapshot with role-based tree structure.
- Interactive and compact modes.
- Element refs for interactive elements.
- `--selector` scoping for partial snapshots.
- maxChars and maxDepth limits.

### Wishlist

**17.1 ARIA Diff**
Compare two snapshots and show what changed. Useful for verifying that an action had the expected effect.
- Command: `cc-browser snapshot diff --before before.txt --after after.txt`
- Returns: elements added, removed, changed.

**17.2 Semantic Snapshot Modes**
Different snapshot formats optimized for different use cases:
- `--format aria` (current) -- full ARIA tree
- `--format summary` -- high-level page description (headings, landmarks, forms, links count)
- `--format forms` -- only form elements and their current values
- `--format links` -- only links with their text and URLs
- `--format tables` -- only table data as structured JSON

**17.3 Incremental Snapshots**
After the first full snapshot, only send the delta (changes) for subsequent snapshots.
- Reduces data transfer for large pages where most content is static.

**17.4 Shadow DOM Support**
Traverse into Shadow DOM trees for web components. Currently, shadow roots may be opaque to the ARIA walker.

**17.5 iframe Content**
Include content from same-origin and cross-origin iframes in the snapshot.
- Requires `all_frames: true` in manifest and cross-frame content script coordination.

---

## 18. Element Interaction Improvements

### Current State
- Click, double-click, type, fill, press, hover, scroll, drag, select, upload.
- Element resolution by ref, text, or CSS selector.
- Human-like delays in content script interactions.

### Wishlist

**18.1 Right-Click / Context Menu**
Trigger right-click context menu and interact with context menu items.
- Command: `cc-browser right-click --ref e5`

**18.2 Keyboard Shortcuts**
Send keyboard shortcuts (Ctrl+C, Ctrl+V, Ctrl+A, etc.).
- Command: `cc-browser press --key "Control+a"` (select all)
- Currently `press` sends single keys. Extend for modifiers + key combos.

**18.3 Clipboard Access**
Read from and write to the clipboard.
- Command: `cc-browser clipboard get` / `cc-browser clipboard set "text"`
- Uses `navigator.clipboard` API in content script.

**18.4 Long Press / Touch Events**
Simulate touch events for mobile-responsive pages.
- Command: `cc-browser touch --ref e5 --duration 1000`

**18.5 Multi-Element Operations**
Select multiple elements and perform batch operations.
- Command: `cc-browser click-all --selector ".notification .dismiss"` (close all notifications)

**18.6 Drag-and-Drop with Coordinates**
Support drag from element to specific page coordinates (not just element-to-element).
- Command: `cc-browser drag --from-ref e5 --to-x 500 --to-y 300`

**18.7 Natural Typing with Typos**
Type text with human-like randomness: variable speed, occasional pauses, optional typo-and-correct behavior.
- Makes typing appear more human for anti-bot purposes.
- Flag: `--human-type` with configurable speed and error rate.

---

## 19. Wait and Timing Intelligence

### Current State
- Wait for: text appear, text disappear, CSS selector, URL change, fixed time.
- Configurable timeout.

### Wishlist

**19.1 Wait for Network Idle**
Wait until no network requests are in-flight for N milliseconds.
- Command: `cc-browser wait --network-idle 2000`
- Useful after navigation or AJAX-heavy interactions.

**19.2 Wait for Element State**
Wait for an element to be visible, hidden, enabled, disabled, or checked.
- Command: `cc-browser wait --ref e5 --state visible`
- Command: `cc-browser wait --selector "#submit" --state enabled`

**19.3 Wait for DOM Stability**
Wait until the DOM stops changing (no mutations for N milliseconds).
- Command: `cc-browser wait --dom-stable 1000`
- Uses MutationObserver to detect when the page has settled.

**19.4 Wait for Animation Complete**
Wait until CSS animations and transitions finish.
- Command: `cc-browser wait --animations-done`

**19.5 Smart Auto-Wait**
Automatically wait for relevant conditions before executing each command. If clicking a button, wait for it to be visible and enabled first.
- Inspired by: Playwright's auto-wait behavior.
- Global flag: `--auto-wait` or per-connection setting.

**19.6 Retry with Backoff**
Retry a failed command with exponential backoff.
- Flag: `--retry 3 --retry-delay 1000`
- Useful for flaky elements that appear asynchronously.

---

## 20. Navigation and Browsing

### Current State
- Navigate to URL, tab management.
- No back/forward, no reload, no history access.

### Wishlist

**20.1 Back / Forward / Reload**
Browser navigation controls.
- Command: `cc-browser back`, `cc-browser forward`, `cc-browser reload`
- Uses `chrome.tabs.goBack()`, `chrome.tabs.goForward()`, `chrome.tabs.reload()`.

**20.2 Browser History Access**
Read browsing history for a connection.
- Command: `cc-browser history --connection linkedin --count 20`
- Uses `chrome.history` API (requires additional permission).

**20.3 Bookmarks Management**
Create and access bookmarks.
- Uses `chrome.bookmarks` API.

**20.4 URL Monitoring**
Watch for URL changes and trigger callbacks.
- Command: `cc-browser watch --url-pattern "*/messaging/*" --action snapshot`
- Content script monitors `location.href` changes.

**20.5 Link Extraction**
Get all links on the current page with their text and URLs.
- Command: `cc-browser links --connection linkedin`
- Returns JSON array: `[{"text": "Home", "url": "https://...", "ref": "e1"}, ...]`

---

## 21. Developer Experience

### Current State
- CLI with JSON output.
- Per-command help not yet implemented.
- Daemon logs to console.

### Wishlist

**21.1 Per-Command Help**
`cc-browser click --help` shows usage, flags, and examples for the click command.
- Already planned in FEATURE_PLAN.md. Just needs implementation.

**21.2 Shell Completion**
Tab completion for bash/zsh/fish with commands, flags, and connection names.
- Generate completion scripts: `cc-browser completion bash > /etc/bash_completion.d/cc-browser`

**21.3 Pretty Output Mode**
Human-readable formatted output instead of raw JSON.
- Flag: `--pretty` or `--format human`
- Example: snapshot shows a readable tree instead of raw JSON.

**21.4 Verbose / Debug Mode**
Show the full request/response chain for debugging.
- Flag: `--verbose` or `-v`
- Shows: HTTP request -> WebSocket message -> extension command -> content script -> result.

**21.5 Config File**
Per-connection configuration (default timeout, auto-wait, pretty output, etc.).
- File: `%LOCALAPPDATA%\cc-director\connections\<name>\config.json`

**21.6 TypeScript/JavaScript SDK**
A programmatic API for calling cc-browser from Node.js code (not just CLI).
- `import { browser } from 'cc-browser'; await browser.connect('linkedin').click({text: 'Submit'});`

---

## 22. Monitoring and Debugging

### Current State
- Daemon logs to console (stdout).
- Lockfile tracks daemon PID and port.
- Connection status tracking (connected/disconnected).

### Wishlist

**22.1 Session Replay**
Record all browser interactions during a session and replay them visually later.
- Captures: screenshots at each step, command/response pairs, timing.
- Command: `cc-browser session-replay --connection linkedin --last`
- Inspired by: Browserless Session Replay.

**22.2 Performance Metrics**
Track command execution times, page load times, element resolution times.
- Command: `cc-browser metrics --connection linkedin`
- Shows: average command latency, slowest commands, total commands executed.

**22.3 Health Check**
Verify a connection is healthy: browser running, extension connected, page responsive.
- Command: `cc-browser health --connection linkedin`
- Returns: { browser: "running", extension: "connected", page: "responsive", url: "..." }

**22.4 Error Classification**
Categorize errors (timeout, element not found, network error, auth expired, CAPTCHA) and suggest fixes.
- Instead of generic "Command failed", return "Element not found. Suggestion: Run a new snapshot, the page may have changed."

**22.5 Daemon Dashboard**
A web-based dashboard showing all connections, their status, recent commands, errors, and performance.
- Served by the daemon on a separate port.
- Shows live connection status, command history, screenshots.

---

## 23. Cloud and Scaling

### Current State
- Single machine, single daemon, single browser per connection.
- No cloud deployment, no remote access, no scaling.

### Wishlist

**23.1 Remote Daemon Access**
Allow CLI to connect to a daemon on a different machine (secure WebSocket).
- Useful for: running automation on a cloud VM while controlling from local CLI.
- Command: `cc-browser --daemon-url wss://remote:9280 status`

**23.2 Connection Sharing**
Allow multiple CLI clients to share a single connection (with locking already in place).
- Current locking system supports this. Just needs cross-machine daemon access.

**23.3 Docker Container**
Package cc-browser + Chrome + extension in a Docker container for cloud deployment.
- Uses Xvfb for virtual display.
- Pre-configured with extensions and native messaging.

**23.4 Connection Pools**
Manage a pool of identical connections for load distribution.
- Use case: 5 LinkedIn connections rotating to avoid rate limits.

---

## 24. Skill System Improvements

### Current State
- Managed skills: markdown files with site-specific instructions.
- Custom skills: per-connection overrides.
- Learned patterns: append-only text patterns.
- Skills are read by Claude, not executed by cc-browser.

### Wishlist

**24.1 Executable Skills**
Skills that contain both instructions (for AI interpretation) AND executable workflow steps (for deterministic replay).
- Hybrid: AI handles the unpredictable parts, deterministic steps handle the routine parts.

**24.2 Skill Versioning**
Track skill versions and changes over time. Roll back to previous versions if a skill update breaks.

**24.3 Skill Marketplace / Sharing**
Share skills between users. A community repository of skills for popular sites.
- `cc-browser skills install twitter-post`
- `cc-browser skills publish my-custom-skill`

**24.4 Skill Testing**
Test skills against a live site to verify they still work.
- Command: `cc-browser skills test linkedin --connection linkedin`
- Runs the skill's test scenarios and reports pass/fail.

**24.5 Skill Generation from Recording**
Record a browser session and auto-generate a skill file from the recording.
- "Show me how to do it once, I'll write the skill for you."

**24.6 Conditional Skill Sections**
Skills that adapt based on page state: "If you see a login page, do X. If you see the feed, do Y."

---

## 25. Security and Privacy

### Current State
- Localhost-only daemon (127.0.0.1).
- Per-connection profile isolation.
- Native messaging security (allowed_origins in manifest).
- No authentication on daemon API.

### Wishlist

**25.1 Daemon API Authentication**
Require a token/key to access the daemon HTTP API.
- Prevents other local processes from controlling the browser.
- Token stored in connection config and passed as header.

**25.2 Command Audit Trail**
Log every command with timestamp, caller, connection, and result.
- Stored per-connection in a tamper-evident log.
- Useful for: security review, debugging, compliance.

**25.3 Sensitive Data Masking**
Automatically mask passwords, tokens, and PII in logs and command responses.
- Never log the content of `--text` for password fields.

**25.4 Connection Encryption**
Encrypt the WebSocket connection between daemon and native host.
- Currently unencrypted on localhost. Low risk but defense-in-depth.

**25.5 Profile Encryption at Rest**
Encrypt the Chrome profile directory when the connection is closed.
- Protects cookies and saved passwords if the machine is compromised.

---

## Competitor Feature Matrix

| Feature | cc-browser | Playwright | Puppeteer | Stagehand | Browser Use | BrowserMCP | Selenium |
|---------|-----------|-----------|----------|----------|------------|-----------|---------|
| Undetectable | [+] | [-] | [-] | [-] | [~] | [+] | [-] |
| Headless mode | [-] | [+] | [+] | [+] | [+] | [-] | [+] |
| ARIA snapshots | [+] | [+] | [-] | [+] | [-] | [-] | [-] |
| Natural language | [-] | [-] | [-] | [+] | [+] | [+] | [-] |
| Recording | [-] | [+] | [-] | [-] | [-] | [-] | [+] |
| SOP import | [-] | [-] | [-] | [-] | [-] | [-] | [-] |
| Network intercept | [-] | [+] | [+] | [+] | [-] | [-] | [+] |
| Self-healing | [-] | [-] | [-] | [+] | [+] | [-] | [-] |
| PDF generation | [-] | [+] | [+] | [+] | [-] | [-] | [-] |
| Multi-browser | [+] | [+] | [-] | [-] | [-] | [-] | [+] |
| Cookie mgmt | [-] | [+] | [+] | [+] | [-] | [-] | [+] |
| Skill system | [+] | [-] | [-] | [-] | [-] | [-] | [-] |
| Connection mgmt | [+] | [-] | [-] | [-] | [-] | [-] | [-] |
| Interactive mode | [-] | [+] | [-] | [-] | [~] | [-] | [-] |
| Parallel execution | [-] | [+] | [+] | [+] | [-] | [-] | [+] |
| MCP protocol | [-] | [+] | [-] | [+] | [-] | [+] | [-] |
| Vision/AI | [-] | [-] | [-] | [+] | [+] | [-] | [-] |
| Full-page screenshot | [-] | [+] | [+] | [+] | [-] | [-] | [+] |
| Download mgmt | [-] | [+] | [+] | [+] | [-] | [-] | [+] |
| Element caching | [-] | [-] | [-] | [+] | [-] | [-] | [-] |
| Human-in-loop | [-] | [-] | [-] | [-] | [~] | [-] | [-] |
| Workflow engine | [-] | [-] | [-] | [~] | [+] | [-] | [-] |

Legend: [+] = strong support, [~] = partial, [-] = not supported

---

## Priority Tiers

### Tier 1: High Impact, Achievable Now
These use the existing extension architecture with minimal changes:

1. **Command batching** (1.1) -- huge speed win
2. **Save screenshot to file** (10.1) -- already planned
3. **Back/Forward/Reload** (20.1) -- trivial chrome API calls
4. **Cookie CRUD** (14.1) -- chrome.cookies already in permissions
5. **Per-command help** (21.1) -- already planned
6. **Wait for network idle** (19.1) -- content script addition
7. **Request blocking** (8.2) -- chrome.declarativeNetRequest
8. **Action recording** (3.1) -- content script event capture
9. **Link extraction** (20.5) -- content script DOM walk
10. **Minimized window mode** (2.2) -- one Chrome flag

### Tier 2: High Impact, Medium Effort
Require meaningful development but transform the tool:

11. **Deterministic replay engine** (3.2)
12. **Interactive shell** (4.1)
13. **Pause and resume** (4.2)
14. **Stable element IDs** (6.1)
15. **Full-page screenshot** (10.2)
16. **Natural language commands** (5.1)
17. **Structured data extraction** (9.1-9.3)
18. **Workflow definition language** (7.1)
19. **MCP server mode** (16.1)
20. **Health check** (22.3)

### Tier 3: Ambitious, High Value
Significant investment, but game-changing:

21. **SOP import and execution** (3.3)
22. **AI-powered extraction** (5.3)
23. **Self-healing selectors** (6.2)
24. **Session replay** (22.1)
25. **Goal-oriented automation** (5.5)
26. **Video recording** (10.6)
27. **Response body capture** (8.3)
28. **Skill generation from recording** (24.5)

### Tier 4: Nice to Have
Lower priority but useful:

29. **Shell completion** (21.2)
30. **Daemon dashboard** (22.5)
31. **Docker container** (23.3)
32. **Profile encryption** (25.5)
33. **Bandwidth throttling** (8.5)
34. **Bookmark management** (20.3)

---

## Research Sources

- [Playwright vs Selenium 2025](https://www.browserless.io/blog/playwright-vs-selenium-2025-browser-automation-comparison)
- [Top 9 Browser Automation Tools 2026](https://www.firecrawl.dev/blog/browser-automation-tools-comparison)
- [11 Best AI Browser Agents 2026](https://www.firecrawl.dev/blog/best-browser-agents)
- [Stagehand v3](https://www.browserbase.com/blog/stagehand-v3)
- [Stagehand GitHub](https://github.com/browserbase/stagehand)
- [Browser Use](https://browser-use.com/)
- [BrowserMCP](https://browsermcp.io/)
- [Browserbase](https://www.browserbase.com/)
- [Steel Browser](https://github.com/steel-dev/steel-browser)
- [Stealth AI Browser Agents Guide 2026](https://o-mega.ai/articles/stealth-for-ai-browser-agents-the-ultimate-2026-guide)
- [Nodriver GitHub](https://github.com/ultrafunkamsterdam/nodriver)
- [Skyvern AI](https://www.skyvern.com/)
- [Axiom.ai](https://axiom.ai/)
- [State of AI Browser Automation 2026](https://www.browserless.io/blog/state-of-ai-browser-automation-2026)
- [Browserless Session Replay](https://www.browserless.io/blog/browserless-new-features-debug-chrome-extensions-replay-2025)
- [Self-Healing Test Automation](https://katalon.com/resources-center/blog/self-healing-test-automation)
- [Hybrid Automation - Browserless](https://docs.browserless.io/baas/interactive-browser-sessions/hybrid-automation)
- [Playwright ARIA Snapshots](https://playwright.dev/docs/aria-snapshots)
- [Skyvern SOP Upload](https://www.skyvern.com/blog/)
- [AI SOP Generator](https://www.glitter.io/blog/process-documentation/ai-sop-generator)
- [Browser Automation Session Management](https://www.skyvern.com/blog/browser-automation-session-management/)
