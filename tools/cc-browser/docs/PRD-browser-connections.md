# PRD: Browser Connections (cc-browser v2)

## Status: Draft
## Date: 2026-03-04

---

## 1. Overview

### Problem

cc-browser v1 uses Playwright to control Chrome via the Chrome DevTools Protocol (CDP). This approach has a fundamental flaw: **CDP-connected browsers are detectable by anti-bot systems**. Cloudflare Turnstile, DataDome, PerimeterX, and similar services detect automation through:

- `navigator.webdriver` flag (set by `--enable-automation`)
- CDP-specific browser artifacts (Runtime.evaluate traces, missing chrome.runtime)
- Playwright's default launch arguments that strip browser features real users have
- Canvas/WebGL fingerprint anomalies from stealth script injection
- Missing or synthetic browser extensions

Despite extensive stealth measures (webdriver masking, canvas noise, plugin spoofing), sophisticated detectors still identify the automation. The cat-and-mouse game of patching detection vectors is unsustainable.

### Solution

Replace CDP/Playwright with a **Chrome Extension + Native Messaging** architecture:

- A Chrome Extension (Manifest V3) runs inside a normal Chrome browser instance
- The extension communicates with a Native Messaging Host via Chrome's built-in messaging channel
- The native host bridges to the cc-browser daemon via WebSocket
- The browser is a completely normal Chrome instance -- no CDP, no `--enable-automation`, no detectable artifacts

This architecture is **undetectable** because the extension operates within Chrome's standard extension APIs, exactly like any user-installed extension. Anti-bot systems cannot distinguish between a user clicking a button and an extension calling `chrome.tabs.update()`.

### Why Now

1. LinkedIn and Reddit have become aggressive with bot detection, requiring manual browser use
2. Cloudflare Turnstile blocks are increasing in frequency across target sites
3. The stealth script maintenance burden is growing with each Chrome update
4. Chrome's Manifest V3 extension APIs now cover all the browser control we need

---

## 2. Goals and Non-Goals

### Goals

- **Undetectable automation**: Browser instances indistinguishable from manual use
- **Connection-per-site model**: Each connection is a named, single-site browser instance
- **Director UI integration**: New "Connections" tab in CC Director for managing connections
- **Tool binding**: cc-linkedin, cc-reddit, etc. bind to named connections
- **Persistent sessions**: Browser profiles persist logins, cookies, and state across restarts
- **Same CLI interface**: `cc-browser navigate`, `cc-browser click`, etc. work identically
- **ARIA snapshots**: Maintain the accessibility tree snapshot system for element refs

### Non-Goals

- **Headless mode**: Extensions do not work in headless Chrome. All connections are visible browser windows.
- **Network body interception**: chrome.webRequest cannot read response bodies. If needed, use a content script + fetch override.
- **Multi-site per connection**: Each connection is bound to one primary site. Use separate connections for separate sites.
- **Automatic health monitoring**: No background health checks on connections. Tools report errors when connections are unavailable.
- **Migration from v1**: Fresh start. Old workspaces are not migrated to connections.
- **Cross-browser support**: Chrome only (Edge uses the same Chromium extension APIs, so it may work, but is not a target).

---

## 3. Architecture

### Current Architecture (v1 -- being replaced)

```
  CLI (cc-browser)
       |
       | HTTP POST
       v
  Daemon (daemon.mjs :9280)
       |
       | Playwright API
       v
  Chrome (CDP pipe or :9222)
       |
       | Chrome DevTools Protocol
       v
  Web Pages
```

**Problem**: The Playwright/CDP layer is detectable.

### New Architecture (v2)

```
  CLI (cc-browser)                     Director UI
       |                                    |
       | HTTP POST                          | (manages connections)
       v                                    v
  Daemon (daemon.mjs :9280) <-----> Connection Registry
       |                            (SQLite or JSON files)
       |
       | WebSocket
       v
  Native Messaging Host (native-host.mjs)
       |
       | stdin/stdout (4-byte length-prefixed JSON)
       v
  Chrome Extension (background.js service worker)
       |
       | chrome.tabs, chrome.scripting, chrome.debugger
       v
  Chrome Browser (normal instance, --load-extension)
       |
       v
  Web Pages
```

### Data Flow: CLI Command Example

```
User runs: cc-browser --connection linkedin navigate --url https://linkedin.com

1. CLI resolves "linkedin" connection from registry
   -> Gets daemon port, verifies connection exists

2. CLI sends HTTP POST to daemon:
   POST http://127.0.0.1:9280/navigate
   { "connection": "linkedin", "url": "https://linkedin.com" }

3. Daemon looks up the WebSocket for the "linkedin" connection
   -> Sends JSON message over WebSocket:
   { "id": 42, "command": "navigate", "params": { "url": "..." } }

4. Native Messaging Host receives WebSocket message
   -> Encodes as 4-byte length-prefixed JSON
   -> Writes to stdout (Chrome's native messaging channel)

5. Chrome Extension (background.js) receives native message
   -> Calls: chrome.tabs.update(tabId, { url: "https://linkedin.com" })
   -> Waits for chrome.tabs.onUpdated "complete" event
   -> Sends response back via native messaging

6. Response flows back: Extension -> Native Host -> WebSocket -> Daemon -> HTTP -> CLI
```

### Data Flow: ARIA Snapshot

```
User runs: cc-browser --connection linkedin snapshot

1-3. Same as above, command reaches the extension

4. Extension (background.js) receives "snapshot" command
   -> Sends message to content.js via chrome.tabs.sendMessage()

5. Content script (content.js) runs in the page context
   -> Builds accessibility tree from DOM
   -> Walks aria roles, names, states
   -> Generates snapshot text with [ref=eN] markers
   -> Sends response back to background.js

6. Response flows back through the chain
```

---

## 4. User Experience

### Director UI: Connections Tab

A new tab appears in the center content TabControl alongside Terminal, Source Control, and Repositories.

```
+------------------+---+------------------------------------------+
| Sidebar          | | | [Terminal] [Source Control] [Connections]  |
|                  | | |                                          |
| - Sessions       | | |  CONNECTIONS                        [+]  |
|   ...            | | |                                          |
|                  | | |  linkedin          linkedin.com     [Open]|
|                  | | |    Tool: cc-linkedin                     |
|                  | | |    Status: Connected                     |
|                  | | |                                          |
|                  | | |  reddit            reddit.com       [Open]|
|                  | | |    Tool: cc-reddit                       |
|                  | | |    Status: Disconnected                  |
|                  | | |                                          |
|                  | | |  dev-studio        dev.mindziestudio.com  |
|                  | | |    Tool: (none)                     [Open]|
|                  | | |    Status: Connected                     |
|                  | | |                                          |
+------------------+---+------------------------------------------+
```

### Add Connection Dialog

Triggered by the [+] button. Simple dialog following VisualStyle.md.

```
+--------------------------------------------+
|  Add Connection                            |
|--------------------------------------------|
|                                            |
|  Name:     [linkedin________________]      |
|                                            |
|  URL:      [https://linkedin.com____]      |
|                                            |
|  Tool:     [cc-linkedin_________|v]        |
|            (optional tool binding)         |
|                                            |
|                         [OK]  [Cancel]     |
+--------------------------------------------+
```

- **Name**: Unique identifier. Lowercase alphanumeric + hyphens. Used in CLI as `--connection <name>`.
- **URL**: The primary site URL. Chrome opens to this URL on first launch.
- **Tool**: Optional dropdown binding to a cc-tool (cc-linkedin, cc-reddit, or none). When a tool is bound, calling that tool automatically uses this connection.

### Flow: Creating a New Connection

1. User clicks [+] in the Connections tab
2. Add Connection dialog opens immediately
3. User fills in name, URL, optional tool binding
4. User clicks OK
5. Connection is registered in the connection store
6. Chrome launches immediately with the extension loaded, navigating to the URL
7. User logs in manually in the browser
8. Connection is now ready -- browser stays open
9. Tools (cc-linkedin, etc.) can now use this connection

### Flow: Daily Usage

1. User opens Director (connections are listed, all show "Disconnected")
2. User clicks [Open] on the "linkedin" connection
3. Chrome launches with the linkedin profile, extension auto-connects
4. Status changes to "Connected" in the UI
5. User (or cc-linkedin) runs commands against the connection
6. When done, user closes the Chrome window
7. Status changes to "Disconnected"
8. Next time, clicking [Open] restores the same profile with cookies/logins intact

### Flow: Tool Usage (cc-linkedin)

```bash
# cc-linkedin internally resolves its connection
cc-linkedin messages --unread

# Equivalent to:
cc-browser --connection linkedin navigate --url https://linkedin.com/messaging
cc-browser --connection linkedin snapshot
# ... process ARIA snapshot ...
```

The tool binding in the connection config tells cc-linkedin which connection to use without the user specifying `--connection` every time.

---

## 5. Connection Model

### Data Model

```javascript
{
  "name": "linkedin",           // Unique identifier (lowercase, alphanumeric + hyphens)
  "url": "https://linkedin.com", // Primary site URL
  "toolBinding": "cc-linkedin", // Optional: which cc-tool uses this connection
  "createdAt": "2026-03-04T10:00:00Z",
  "browser": "chrome",          // Browser kind (chrome, edge, brave)
  "profileDir": null,           // null = isolated profile (default), or system profile name
  "daemonPort": 9280,           // Which daemon manages this connection
  "extensionPort": null,        // WebSocket port assigned to this connection's native host
  "status": "disconnected"      // Runtime state: disconnected | connecting | connected
}
```

### Storage Layout

```
%LOCALAPPDATA%\cc-director\connections\
  connections.json               # Connection registry (array of connection configs)
  linkedin\                      # Per-connection Chrome profile directory
    Default\                     # Chrome profile data (cookies, localStorage, etc.)
    ...
  reddit\
    Default\
    ...
  dev-studio\
    Default\
    ...
```

Each connection gets its own Chrome user data directory under `connections\<name>\`. This provides:

- **Complete isolation** between connections (no cookie leakage)
- **Persistent logins** that survive browser restarts
- **Independent extension state** per connection

### Lifecycle States

```
  [disconnected] ---> [connecting] ---> [connected]
       ^                   |                |
       |                   v                |
       +--- (timeout) -----+                |
       |                                    |
       +-------- (browser closed) ----------+
```

- **disconnected**: No Chrome process running for this connection
- **connecting**: Chrome is launching, extension is establishing native messaging
- **connected**: Extension is connected via native messaging, ready for commands

### Connection Registry Operations

| Operation | Trigger | Effect |
|-----------|---------|--------|
| Create | Add Connection dialog | Creates entry in connections.json, creates profile dir |
| Open | [Open] button or first tool command | Launches Chrome with `--load-extension` and `--user-data-dir` |
| Close | User closes Chrome window | Extension disconnects, status -> disconnected |
| Delete | Right-click -> Delete (with confirmation) | Removes entry AND profile directory |
| Edit | Right-click -> Edit | Opens edit dialog for URL/tool binding |

---

## 6. Extension Design

### Manifest (manifest.json)

```json
{
  "manifest_version": 3,
  "name": "CC Browser Bridge",
  "version": "1.0.0",
  "description": "Bridge between cc-browser CLI and Chrome",
  "permissions": [
    "tabs",
    "scripting",
    "activeTab",
    "nativeMessaging",
    "debugger",
    "downloads",
    "storage"
  ],
  "host_permissions": [
    "<all_urls>"
  ],
  "background": {
    "service_worker": "background.js",
    "type": "module"
  },
  "content_scripts": [
    {
      "matches": ["<all_urls>"],
      "js": ["content.js"],
      "run_at": "document_idle",
      "all_frames": false
    }
  ],
  "nativeMessaging": {
    "name": "com.cc_browser.bridge"
  }
}
```

### Background Script (background.js)

The service worker is the command router. It:

1. Connects to the native messaging host on startup
2. Receives commands as JSON messages
3. Dispatches to the appropriate chrome.* API
4. Sends responses back through native messaging

Key responsibilities:
- Tab management (create, close, focus, list)
- Navigation (chrome.tabs.update with URL)
- Script injection (chrome.scripting.executeScript)
- Screenshot capture (chrome.tabs.captureVisibleTab)
- Message relay to/from content scripts
- Connection health monitoring (ping/pong)

### Content Script (content.js)

Runs in every page. Handles:

1. **ARIA snapshots**: Walks the DOM accessibility tree, builds role-based snapshot
2. **Element interaction**: Click, type, hover via DOM APIs
3. **Text extraction**: innerText, textContent for page reading
4. **Element resolution**: Maintains ref -> element mapping from snapshots
5. **Form filling**: Programmatic form interaction
6. **Scroll operations**: scrollIntoView, wheel events

The content script communicates with background.js via `chrome.runtime.onMessage` / `chrome.runtime.sendMessage`.

### Message Protocol (Extension <-> Background)

```javascript
// Command from background to content script
{
  "type": "command",
  "id": 42,
  "command": "snapshot",
  "params": {
    "interactive": true,
    "compact": false
  }
}

// Response from content script to background
{
  "type": "response",
  "id": 42,
  "success": true,
  "data": {
    "snapshot": "- navigation \"Main\":\n  - link \"Home\" [ref=e1]\n  ...",
    "refs": { "e1": { "role": "link", "name": "Home" } },
    "stats": { "chars": 1234, "lines": 45, "refs": 12, "interactive": 8 }
  }
}

// Error response
{
  "type": "response",
  "id": 42,
  "success": false,
  "error": "Element ref e5 not found. Run a new snapshot."
}
```

---

## 7. Native Messaging

### How Native Messaging Works

Chrome's Native Messaging is a built-in IPC mechanism that allows extensions to communicate with native applications on the user's machine. It uses stdin/stdout with a specific binary protocol.

### Host Registration (Windows)

The native messaging host must be registered in the Windows registry:

```
HKEY_CURRENT_USER\Software\Google\Chrome\NativeMessagingHosts\com.cc_browser.bridge
  (Default) = "C:\Users\<user>\AppData\Local\cc-director\native-host\com.cc_browser.bridge.json"
```

Host manifest file (`com.cc_browser.bridge.json`):

```json
{
  "name": "com.cc_browser.bridge",
  "description": "CC Browser native messaging host",
  "path": "C:\\Users\\<user>\\AppData\\Local\\cc-director\\bin\\cc-browser-native-host.cmd",
  "type": "stdio",
  "allowed_origins": [
    "chrome-extension://<extension-id>/"
  ]
}
```

The `allowed_origins` field is set during installation. Since we load the extension unpacked via `--load-extension`, the extension ID is deterministic (derived from the extension directory path).

### Wire Protocol

Native Messaging uses a 4-byte little-endian length prefix followed by JSON:

```
[4 bytes: message length (LE uint32)] [N bytes: UTF-8 JSON message]
```

Reading from stdin:
```javascript
function readMessage(stream) {
  return new Promise((resolve, reject) => {
    // Read 4-byte header
    const headerBuf = Buffer.alloc(4);
    let headerRead = 0;

    function onData(chunk) {
      // ... accumulate 4 header bytes, then read messageLength bytes
    }

    stream.on('data', onData);
  });
}
```

Writing to stdout:
```javascript
function writeMessage(stream, obj) {
  const json = JSON.stringify(obj);
  const buf = Buffer.from(json, 'utf-8');
  const header = Buffer.alloc(4);
  header.writeUInt32LE(buf.length, 0);
  stream.write(header);
  stream.write(buf);
}
```

### Native Host Process (native-host.mjs)

The native host is a Node.js process that:

1. Reads native messages from stdin (from Chrome extension)
2. Connects to the daemon via WebSocket
3. Bridges messages bidirectionally

```
Chrome Extension <-- stdin/stdout --> native-host.mjs <-- WebSocket --> daemon.mjs
```

Lifecycle:
- Chrome launches the native host when the extension calls `chrome.runtime.connectNative()`
- The host stays alive as long as the native messaging port is open
- When Chrome closes or the extension disconnects, the host process exits
- One native host process per connection

### Message Size Limit

Chrome's Native Messaging has a **1 MB message size limit** per message. This affects:
- Screenshots (base64-encoded PNGs can exceed 1MB)
- Large ARIA snapshots
- Full-page HTML extraction

Mitigation: For screenshots, use JPEG with quality reduction. For large snapshots, use truncation with `maxChars`. For HTML, use selector-based extraction rather than full page.

---

## 8. Daemon Changes

### New Module: transport.mjs

Replaces the Playwright-based routing with extension-based routing.

```javascript
// transport.mjs -- WebSocket server for native host connections

import { WebSocketServer } from 'ws';

// Map of connection name -> WebSocket
const connections = new Map();

export function createTransportServer(port) {
  const wss = new WebSocketServer({ port });

  wss.on('connection', (ws, req) => {
    const connectionName = new URL(req.url, 'http://localhost').searchParams.get('connection');
    connections.set(connectionName, ws);

    ws.on('close', () => {
      connections.delete(connectionName);
    });
  });

  return wss;
}

export async function sendCommand(connectionName, command, params) {
  const ws = connections.get(connectionName);
  if (!ws) {
    throw new Error(`Connection "${connectionName}" is not connected. Open it first.`);
  }

  const id = nextId++;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    ws.send(JSON.stringify({ id, command, params }));
  });
}
```

### Daemon Route Changes

The daemon HTTP routes remain the same (`POST /navigate`, `POST /click`, etc.) but the handler implementations change:

**Before (v1)**:
```javascript
'POST /navigate': async (req, res, params, body) => {
  const cdpUrl = getActiveCdpUrl();
  const result = await navigateViaPlaywright({ cdpUrl, targetId: body.tab, url: body.url });
  jsonSuccess(res, result);
}
```

**After (v2)**:
```javascript
'POST /navigate': async (req, res, params, body) => {
  const conn = resolveConnection(body);
  const result = await sendCommand(conn, 'navigate', { url: body.url, tab: body.tab });
  jsonSuccess(res, result);
}
```

### What Gets Removed

- All Playwright imports (`playwright-core` dependency removed entirely)
- `session.mjs` (Playwright connection management)
- `interactions.mjs` (Playwright-based interactions)
- `snapshot.mjs` (Playwright-based snapshots -- logic moves to content.js)
- `human-mode.mjs` (human-like delays -- moves to content.js)
- `captcha.mjs` (captcha detection -- may be reimplemented later)
- `recorder.mjs` / `replay.mjs` (recording -- deferred to future phase)
- `vision.mjs` (screenshot vision -- deferred)

### What Gets Added

- `transport.mjs` (WebSocket server for native host connections)
- `connections.mjs` (connection registry CRUD)
- `chrome-launch.mjs` (Chrome launch with `--load-extension`, no Playwright)

### What Stays (Modified)

- `daemon.mjs` (HTTP server framework, route structure -- handlers rewritten)
- `cli.mjs` (command parser -- `--workspace` replaced with `--connection`)
- `chrome.mjs` (Chrome executable detection -- simplified, no CDP)

---

## 9. CLI Changes

### Connection Flag

The `--workspace` flag is replaced by `--connection`:

```bash
# Old (v1)
cc-browser start --workspace linkedin
cc-browser navigate --url https://linkedin.com

# New (v2)
cc-browser --connection linkedin navigate --url https://linkedin.com
```

The `--connection` flag is a global flag that appears before the subcommand. If omitted, the daemon uses the only active connection (or errors if there are multiple).

### New Subcommands

```bash
# Connection management
cc-browser connections list              # List all connections
cc-browser connections add <name> --url <url> [--tool <tool>]  # Add connection
cc-browser connections remove <name>     # Remove connection (with confirmation)
cc-browser connections open <name>       # Launch Chrome for this connection
cc-browser connections close <name>      # Close Chrome for this connection
cc-browser connections status [<name>]   # Show connection status

# Existing commands (unchanged interface)
cc-browser --connection <name> navigate --url <url>
cc-browser --connection <name> snapshot [--interactive]
cc-browser --connection <name> click --ref <ref>
cc-browser --connection <name> type --ref <ref> --text "hello"
cc-browser --connection <name> screenshot
cc-browser --connection <name> tabs
# ... all other commands remain the same
```

### Removed Subcommands

- `cc-browser start` (replaced by `connections open`)
- `cc-browser stop` (replaced by `connections close`)
- `cc-browser mode` (no more fast/human/stealth modes -- all interactions are natural)
- `cc-browser workspace` commands (replaced by `connections` commands)

---

## 10. Chrome API Mapping

Every Playwright command from v1 maps to a chrome.* extension API in v2.

### Navigation

| v1 (Playwright) | v2 (Extension API) | Notes |
|-----|-----|-------|
| `page.goto(url)` | `chrome.tabs.update(tabId, { url })` + `onUpdated` listener | Wait for `status: 'complete'` |
| `page.reload()` | `chrome.tabs.reload(tabId)` | |
| `page.goBack()` | `chrome.tabs.goBack(tabId)` | |
| `page.goForward()` | `chrome.tabs.goForward(tabId)` | |
| `page.url()` | `chrome.tabs.get(tabId).url` | |
| `page.title()` | `chrome.tabs.get(tabId).title` | |

### Tab Management

| v1 (Playwright) | v2 (Extension API) | Notes |
|-----|-----|-------|
| `browser.contexts()[0].pages()` | `chrome.tabs.query({ windowId })` | |
| `context.newPage()` | `chrome.tabs.create({ url, windowId })` | |
| `page.close()` | `chrome.tabs.remove(tabId)` | |
| `page.bringToFront()` | `chrome.tabs.update(tabId, { active: true })` | |

### Element Interaction (via content script)

| v1 (Playwright) | v2 (Content Script) | Notes |
|-----|-----|-------|
| `locator.click()` | `element.click()` | Content script dispatches MouseEvent |
| `locator.dblclick()` | `element.dispatchEvent(new MouseEvent('dblclick'))` | |
| `locator.hover()` | `element.dispatchEvent(new MouseEvent('mouseover'))` | + mouseenter |
| `locator.fill(text)` | Set `element.value`, dispatch `input`+`change` events | |
| `locator.type(text)` | Dispatch `keydown`/`keypress`/`keyup` per character | |
| `locator.press('Enter')` | `element.dispatchEvent(new KeyboardEvent('keydown', {key:'Enter'}))` | |
| `locator.selectOption(values)` | Set `select.value`, dispatch `change` | |
| `locator.setChecked(bool)` | Set `input.checked`, dispatch `change`+`click` | |
| `locator.scrollIntoViewIfNeeded()` | `element.scrollIntoView({ block: 'nearest' })` | |
| `locator.boundingBox()` | `element.getBoundingClientRect()` | |
| `locator.evaluate(fn)` | Direct DOM access in content script | |
| `locator.setInputFiles(paths)` | DataTransfer + `change` event | See note below |
| `page.mouse.wheel(dx, dy)` | `window.scrollBy(dx, dy)` or `element.scrollBy()` | |

**File upload note**: `setInputFiles` requires creating a `DataTransfer` object with `File` objects. The file data must be sent from the native host to the content script as base64, then reconstructed into a `File`. This is a complex operation but achievable.

### Drag and Drop (via content script)

| v1 (Playwright) | v2 (Content Script) | Notes |
|-----|-----|-------|
| `locator.dragTo(target)` | Dispatch `dragstart`, `dragover`, `drop`, `dragend` | Full HTML5 drag event sequence |

### Screenshots

| v1 (Playwright) | v2 (Extension API) | Notes |
|-----|-----|-------|
| `page.screenshot()` | `chrome.tabs.captureVisibleTab()` | Returns data URL (PNG/JPEG) |
| `page.screenshot({ fullPage: true })` | Content script scroll-and-stitch | Multiple captures stitched together |
| `locator.screenshot()` | Capture + crop in content script | Get bounding rect, crop from full capture |

### JavaScript Evaluation

| v1 (Playwright) | v2 (Content Script) | Notes |
|-----|-----|-------|
| `page.evaluate(fn)` | `chrome.scripting.executeScript({ func })` | Runs in page's isolated world |
| `page.evaluate(fn)` (main world) | Content script with `window.eval()` | For accessing page-scope variables |

### ARIA Snapshot

| v1 (Playwright) | v2 (Content Script) | Notes |
|-----|-----|-------|
| `page.locator('body').ariaSnapshot()` | Custom DOM walker in content.js | Walk DOM tree, read `role`, `aria-*` attributes, `computedRole` |

The ARIA snapshot logic moves entirely to the content script. The existing `buildRoleSnapshotFromAriaSnapshot()` parsing from `snapshot.mjs` is reused -- the content script generates the same text format.

### Wait Operations

| v1 (Playwright) | v2 (Content Script) | Notes |
|-----|-----|-------|
| `page.waitForURL(url)` | Poll `location.href` or use `popstate`/`hashchange` | |
| `page.waitForLoadState('load')` | `document.readyState` check or `load` event | |
| `page.getByText(text).waitFor()` | MutationObserver watching for text | |
| `page.waitForFunction(fn)` | `requestAnimationFrame` polling loop | |
| `page.waitForTimeout(ms)` | `setTimeout` | |

### Viewport

| v1 (Playwright) | v2 (Extension API) | Notes |
|-----|-----|-------|
| `page.setViewportSize({w, h})` | `chrome.windows.update(winId, {width, height})` | Resizes window, not viewport |
| `page.viewportSize()` | Content script: `{ innerWidth, innerHeight }` | |

### Text/HTML Extraction

| v1 (Playwright) | v2 (Content Script) | Notes |
|-----|-----|-------|
| `locator.textContent()` | `element.textContent` | |
| `locator.innerHTML()` | `element.innerHTML` | |
| `page.content()` | `document.documentElement.outerHTML` | |

### Session/Cookie Management

| v1 (Playwright) | v2 (Extension API) | Notes |
|-----|-----|-------|
| (not available in v1) | `chrome.cookies.get/set/remove()` | New capability |
| Profile persistence | `--user-data-dir` flag on Chrome launch | Same approach |

### Network Monitoring

| v1 (Playwright) | v2 (Extension API) | Notes |
|-----|-----|-------|
| `page.on('request')` | `chrome.webRequest.onBeforeRequest` | URL/method only, no body |
| `page.on('response')` | `chrome.webRequest.onCompleted` | Status code, no body |
| Request body reading | Not possible via chrome.webRequest | Use content script fetch override if needed |

---

## 11. cc-tool Integration

### Tool Binding Resolution

When a cc-tool (e.g., cc-linkedin) needs to interact with a browser, it resolves its connection:

```javascript
// In cc-linkedin:
function getConnectionName() {
  // 1. Explicit --connection flag (highest priority)
  if (args.connection) return args.connection;

  // 2. Read connection registry, find binding for "cc-linkedin"
  const connections = readConnectionRegistry();
  const bound = connections.find(c => c.toolBinding === 'cc-linkedin');
  if (bound) return bound.name;

  // 3. Error: no connection configured
  throw new Error(
    'No connection configured for cc-linkedin. ' +
    'Create one: cc-browser connections add linkedin --url https://linkedin.com --tool cc-linkedin'
  );
}
```

### Tool-Connection Binding Table

| Tool | Default Connection Name | URL |
|------|------------------------|-----|
| cc-linkedin | linkedin | https://linkedin.com |
| cc-reddit | reddit | https://reddit.com |
| (general browsing) | (user-defined) | (any URL) |

### Connection Registry API

Tools read the connection registry via a shared module:

```javascript
// connections.mjs (shared)
import { readFileSync, existsSync } from 'fs';
import { join } from 'path';
import { homedir } from 'os';

const REGISTRY_PATH = join(
  process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local'),
  'cc-director', 'connections', 'connections.json'
);

export function readConnectionRegistry() {
  if (!existsSync(REGISTRY_PATH)) return [];
  return JSON.parse(readFileSync(REGISTRY_PATH, 'utf-8'));
}

export function findConnectionByTool(toolName) {
  return readConnectionRegistry().find(c => c.toolBinding === toolName);
}
```

---

## 12. Security Considerations

### Profile Isolation

Each connection has its own Chrome user data directory. This means:
- Cookies, localStorage, and IndexedDB are completely separate
- One connection cannot access another connection's data
- A compromised site in one connection cannot affect others

### Native Messaging Security

Chrome's native messaging has built-in security:
- Only the extension ID listed in `allowed_origins` can connect
- The native host path is registered in the Windows registry (requires user/admin access to modify)
- Communication is local only (stdin/stdout, no network)

### Localhost-Only Daemon

The daemon HTTP server binds to `127.0.0.1` only:
```javascript
server.listen(port, '127.0.0.1', () => { ... });
```

No remote connections are accepted. The WebSocket server for native host connections also binds to localhost only.

### Extension Permissions

The extension requests only the permissions it needs:
- `tabs`: Tab management (create, close, query, update)
- `scripting`: Inject content scripts for DOM interaction
- `activeTab`: Access the active tab
- `nativeMessaging`: Communicate with the native host
- `debugger`: chrome.debugger API (fallback for complex operations)
- `downloads`: File download management
- `storage`: Extension state persistence
- `<all_urls>`: Content script injection on any page

### No External Network Access

The extension and native host never make external network requests. All communication is:
- Extension <-> Native Host: stdin/stdout (OS-level IPC)
- Native Host <-> Daemon: WebSocket on localhost
- Daemon <-> CLI: HTTP on localhost

---

## 13. Known Limitations

### No Headless Mode

Chrome extensions do not work in headless mode. All connections require a visible browser window. This is actually a feature for our use case -- headless browsers are a major bot detection signal.

### 1 MB Native Messaging Limit

Each native messaging message is limited to 1 MB. This affects:
- **Screenshots**: A full-page 1920x1080 PNG is ~2-4 MB. Mitigation: use JPEG with 80% quality (~200-400 KB) or chunked transfer.
- **Large DOM snapshots**: Very complex pages might generate >1 MB snapshots. Mitigation: `maxChars` truncation (already implemented in v1).
- **HTML extraction**: Full-page `outerHTML` can exceed 1 MB. Mitigation: selector-based extraction.

### No Response Body Interception

`chrome.webRequest` can observe request/response metadata (URLs, headers, status codes) but cannot read response bodies. This differs from Playwright which can intercept full request/response content.

If response body access is needed in the future, options:
1. Content script overriding `fetch()` and `XMLHttpRequest` to capture responses
2. `chrome.debugger` API (but this triggers "is being debugged" bar -- detectable)

### No Multi-Frame Snapshot

Content scripts run in the top frame by default. Cross-origin iframes require separate content script injection with `all_frames: true` in the manifest. Same-origin iframes can be accessed directly.

### Extension Service Worker Lifecycle

Manifest V3 service workers can be terminated by Chrome after ~30 seconds of inactivity. Mitigation:
- Keep the native messaging port open (counts as activity)
- Use `chrome.alarms` for periodic keepalive if needed

### Single Window Per Connection

Each connection manages one Chrome window. Multiple tabs within that window are supported, but the primary model is one tab per connection (matching the one-site-per-connection design).

---

## 14. Testing Strategy

### Unit Tests

| Component | Test Focus |
|-----------|-----------|
| `connections.mjs` | CRUD operations, validation, registry read/write |
| `transport.mjs` | WebSocket message encoding/decoding, connection tracking |
| `chrome-launch.mjs` | Chrome path detection, argument building, extension loading |
| `native-host.mjs` | 4-byte length-prefix encode/decode, message routing |
| `content.js` | ARIA snapshot generation, element ref resolution, DOM interaction |
| `background.js` | Command dispatch, tab management, message routing |
| `cli.mjs` | Argument parsing, connection resolution |

### Integration Tests

| Test | What It Validates |
|------|-------------------|
| Native messaging round-trip | Extension -> native host -> daemon -> response chain |
| Connection lifecycle | Create -> open -> connected -> close -> disconnected |
| ARIA snapshot accuracy | Content script snapshot matches expected output for test pages |
| Element interaction | Click, type, scroll via content script on test pages |
| Screenshot capture | `captureVisibleTab` returns valid image data |
| Multi-connection | Two connections operating independently |

### End-to-End Tests

| Test | What It Validates |
|------|-------------------|
| Full CLI flow | `cc-browser connections add` -> `open` -> `navigate` -> `snapshot` -> `click` |
| Tool binding | cc-linkedin resolves connection and executes commands |
| Profile persistence | Close and reopen connection, verify cookies survive |
| Large page handling | Snapshot and interaction on complex real-world pages |

### Bot Detection Tests

| Test | What It Validates |
|------|-------------------|
| `navigator.webdriver` | Must be `undefined` (not `true` or `false`) |
| `chrome.runtime` | Must exist and behave normally |
| Canvas fingerprint | Must match normal Chrome (no noise artifacts) |
| WebGL renderer | Must report real GPU (not "SwiftShader") |
| Playwright artifacts | CDP-specific globals must not exist |
| Cloudflare Turnstile | Must pass challenge page on test site |
| Plugin enumeration | `navigator.plugins` must have standard entries |

---

## 15. Implementation Phases

### Phase 1: Extension Core + Native Messaging

Build the minimal extension and native messaging pipeline.

**Deliverables:**
- `extension/manifest.json` (MV3)
- `extension/background.js` (service worker with native messaging)
- `native-host/native-host.mjs` (stdin/stdout <-> WebSocket bridge)
- `native-host/com.cc_browser.bridge.json` (host manifest)
- Registry setup script (Windows)
- Unit tests for message encoding/decoding

**Validation:** Extension connects to native host, sends ping, receives pong.

### Phase 2: Content Script + ARIA Snapshots

Port the snapshot system to a content script.

**Deliverables:**
- `extension/content.js` (DOM walker, ARIA tree builder, ref mapping)
- Snapshot command handling in `background.js`
- Port `buildRoleSnapshotFromAriaSnapshot()` logic to content script
- Interactive and compact snapshot modes

**Validation:** `cc-browser snapshot` returns same-format output as v1 on test pages.

### Phase 3: DOM Interactions

Implement click, type, hover, scroll, and other interactions in the content script.

**Deliverables:**
- Element resolution (ref -> DOM element)
- Click, double-click, right-click
- Type, fill, press key
- Hover, scroll, drag
- Select option, checkbox
- Wait operations (text appear/disappear, URL change)

**Validation:** All interaction commands work on a test page with forms, buttons, and links.

### Phase 4: Daemon Rewrite

Replace Playwright-based daemon with extension-based transport.

**Deliverables:**
- `src/transport.mjs` (WebSocket server)
- `src/connections.mjs` (connection registry)
- `src/chrome-launch.mjs` (Chrome launch with `--load-extension`)
- Rewritten `src/daemon.mjs` (same HTTP routes, new handlers)
- Updated `src/cli.mjs` (`--connection` flag, `connections` subcommands)

**Validation:** CLI commands work end-to-end through the new pipeline.

### Phase 5: Director UI - Connections Tab

Build the WPF UI for connection management.

**Deliverables:**
- `ConnectionsView.xaml` + `ConnectionsView.xaml.cs` (tab content)
- `AddConnectionDialog.xaml` + `AddConnectionDialog.xaml.cs`
- `ConnectionViewModel.cs` (data binding model)
- `ConnectionManager.cs` (business logic)
- Integration into `MainWindow.xaml` tab control

**Validation:** Can create, list, open, close, and delete connections from the UI.

### Phase 6: Tool Integration

Wire up cc-linkedin and cc-reddit to use connections.

**Deliverables:**
- Shared `connections.mjs` module for tool binding resolution
- cc-linkedin updated to use `--connection` (or auto-resolve via binding)
- cc-reddit updated to use `--connection` (or auto-resolve via binding)
- Tool binding validation (error if connection not found)

**Validation:** `cc-linkedin messages --unread` works through a connection.

### Phase 7: Polish + Bot Detection Verification

Final hardening, testing, and cleanup.

**Deliverables:**
- Bot detection test suite (navigator.webdriver, canvas, WebGL, plugins)
- Cloudflare Turnstile pass verification
- Screenshot handling (JPEG compression for 1MB limit)
- Error messages and edge cases
- Documentation updates (cli-reference.md, README)
- Remove cc-browser-archived after confidence period

**Validation:** All bot detection tests pass. LinkedIn and Reddit work without detection.

---

## Appendix A: Extension ID Determinism

When loading an unpacked extension via `--load-extension=<path>`, Chrome generates a deterministic extension ID based on the absolute path of the extension directory. This means:

- The extension ID is stable as long as the extension path does not change
- The `allowed_origins` in the native messaging host manifest can be set during installation
- If the user moves the extension directory, the ID changes and native messaging breaks (re-registration required)

To get the extension ID:
1. Load the extension once with `--load-extension`
2. Navigate to `chrome://extensions`
3. Copy the ID
4. Or compute it: SHA-256 of the absolute path, first 32 chars, mapped to a-p

The installation script will handle this automatically.

## Appendix B: Chrome Launch Arguments (v2)

```bash
chrome.exe \
  --user-data-dir="%LOCALAPPDATA%\cc-director\connections\<name>" \
  --load-extension="<path-to-extension-dir>" \
  --no-first-run \
  --no-default-browser-check \
  --disable-features=TranslateUI \
  --disable-sync
```

Note what is NOT included:
- No `--enable-automation` (the #1 detection signal)
- No `--remote-debugging-port` (no CDP exposure)
- No `--disable-extensions` (extension is required)
- No `--no-sandbox` (no warning bar)
- No `--disable-background-networking` (real browsers have this)
- No `--disable-component-extensions-with-background-pages` (detectable)

The browser looks exactly like a normal user-installed Chrome with one extension.

## Appendix C: File Structure (Final)

```
tools/cc-browser/
  package.json                    # v2.0.0, no playwright-core dependency
  docs/
    PRD-browser-connections.md    # This document
    RESEARCH-extension-native-messaging.md
  src/
    cli.mjs                       # CLI client (--connection flag)
    daemon.mjs                    # HTTP daemon (WebSocket transport)
    transport.mjs                 # WebSocket server for native hosts
    connections.mjs               # Connection registry CRUD
    chrome-launch.mjs             # Chrome launcher (--load-extension)
  extension/
    manifest.json                 # MV3 manifest
    background.js                 # Service worker (command router)
    content.js                    # Content script (DOM interaction, snapshots)
  native-host/
    native-host.mjs               # stdin/stdout <-> WebSocket bridge
    com.cc_browser.bridge.json    # Native messaging host manifest
    install.mjs                   # Registry setup script
  test/
    unit/
    integration/

tools/cc-browser-archived/        # v1 (Playwright-based) -- reference only
  src/
    daemon.mjs
    session.mjs
    interactions.mjs
    snapshot.mjs
    ...
```
