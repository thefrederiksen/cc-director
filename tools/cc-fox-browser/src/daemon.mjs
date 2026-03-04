#!/usr/bin/env node
// CC Fox Browser - HTTP Daemon Server
// Camoufox browser automation for Claude Code
// Usage: node daemon.mjs [--port 9380]

import { createServer } from 'http';
import { parse as parseUrl } from 'url';
import { existsSync, writeFileSync, unlinkSync, mkdirSync } from 'fs';
import { join } from 'path';
import { homedir } from 'os';

import {
  launchCamoufox,
  stopBrowser,
  listTabsAsync,
  openTab,
  closeTab,
  focusTab,
  isRunning,
  getWorkspace,
  getPage,
  getTabId,
} from './browser.mjs';
import { getCurrentMode, setCurrentMode, ensurePageState } from './session.mjs';
import { click, type, pressKey, navigate, reload, goBack, goForward, evaluate, waitFor, takeScreenshot } from './interactions.mjs';
import { snapshot, getPageInfo, getTextContent } from './snapshot.mjs';

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

const DEFAULT_DAEMON_PORT = 9380;

function getLockfilePath() {
  const localAppData = process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local');
  return join(localAppData, 'cc-fox-browser', 'daemon.lock');
}

function writeLockfile(port, workspace) {
  const lockPath = getLockfilePath();
  const lockDir = join(lockPath, '..');
  if (!existsSync(lockDir)) {
    mkdirSync(lockDir, { recursive: true });
  }
  const data = {
    port,
    workspace: workspace || null,
    pid: process.pid,
    startedAt: new Date().toISOString(),
  };
  writeFileSync(lockPath, JSON.stringify(data, null, 2));
  console.log(`[cc-fox-browser] Lockfile written: ${lockPath}`);
}

function removeLockfile() {
  const lockPath = getLockfilePath();
  if (existsSync(lockPath)) {
    unlinkSync(lockPath);
    console.log(`[cc-fox-browser] Lockfile removed: ${lockPath}`);
  }
}

let actualDaemonPort = DEFAULT_DAEMON_PORT;

// ---------------------------------------------------------------------------
// JSON Response Helpers
// ---------------------------------------------------------------------------

function jsonResponse(res, statusCode, data) {
  res.writeHead(statusCode, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(data));
}

function jsonError(res, statusCode, message) {
  jsonResponse(res, statusCode, { success: false, error: message });
}

function jsonSuccess(res, data = {}) {
  jsonResponse(res, 200, { success: true, ...data });
}

// ---------------------------------------------------------------------------
// Request Body Parser
// ---------------------------------------------------------------------------

async function parseBody(req) {
  return new Promise((resolve, reject) => {
    let body = '';
    req.on('data', (chunk) => {
      body += chunk;
      if (body.length > 1024 * 1024) {
        reject(new Error('Request body too large'));
      }
    });
    req.on('end', () => {
      try {
        resolve(body ? JSON.parse(body) : {});
      } catch (err) {
        const preview = body.length > 100 ? body.slice(0, 100) + '...' : body;
        reject(new Error(`Invalid JSON: ${err.message} (body: ${preview})`));
      }
    });
    req.on('error', reject);
  });
}

// ---------------------------------------------------------------------------
// Session Validation
// ---------------------------------------------------------------------------

function validateSession() {
  if (!isRunning()) {
    return { valid: false, error: 'No browser session active. Run "start" first.' };
  }
  return { valid: true };
}

// ---------------------------------------------------------------------------
// Route Handlers
// ---------------------------------------------------------------------------

const routes = {
  // Status
  'GET /': async (req, res) => {
    const running = isRunning();
    const workspace = getWorkspace();
    let tabs = [];
    if (running) {
      tabs = await listTabsAsync();
    }

    jsonSuccess(res, {
      daemon: 'running',
      daemonPort: actualDaemonPort,
      browser: running ? 'connected' : 'not running',
      browserEngine: 'camoufox',
      workspace,
      mode: getCurrentMode(),
      tabs,
    });
  },

  // Start browser
  'POST /start': async (req, res, body) => {
    const workspace = body.workspace || 'default';
    const headless = !!body.headless;

    if (body.mode) {
      setCurrentMode(body.mode);
    }

    const result = await launchCamoufox({ workspace, headless });

    // Get async tab info
    const tabs = await listTabsAsync();

    jsonSuccess(res, {
      started: result.started,
      workspace: result.workspace,
      browserEngine: 'camoufox',
      mode: getCurrentMode(),
      tabs,
      activeTab: result.activeTab,
    });
  },

  // Stop browser
  'POST /stop': async (req, res) => {
    const result = await stopBrowser();
    jsonSuccess(res, result);
  },

  // Navigate
  'POST /navigate': async (req, res, body) => {
    const v = validateSession();
    if (!v.valid) return jsonError(res, 400, v.error);

    const result = await navigate({
      tabId: body.tab,
      url: body.url,
      waitUntil: body.waitUntil,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, result);
  },

  // Reload
  'POST /reload': async (req, res, body) => {
    const v = validateSession();
    if (!v.valid) return jsonError(res, 400, v.error);

    const result = await reload({
      tabId: body.tab,
      waitUntil: body.waitUntil,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, result);
  },

  // Go back
  'POST /back': async (req, res, body) => {
    const v = validateSession();
    if (!v.valid) return jsonError(res, 400, v.error);

    const result = await goBack({
      tabId: body.tab,
      waitUntil: body.waitUntil,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, result);
  },

  // Go forward
  'POST /forward': async (req, res, body) => {
    const v = validateSession();
    if (!v.valid) return jsonError(res, 400, v.error);

    const result = await goForward({
      tabId: body.tab,
      waitUntil: body.waitUntil,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, result);
  },

  // Snapshot
  'POST /snapshot': async (req, res, body) => {
    const v = validateSession();
    if (!v.valid) return jsonError(res, 400, v.error);

    const result = await snapshot({
      tabId: body.tab,
      interactive: body.interactive,
      compact: body.compact,
      maxDepth: body.maxDepth,
      maxChars: body.maxChars,
    });
    jsonSuccess(res, result);
  },

  // Page info
  'POST /info': async (req, res, body) => {
    const v = validateSession();
    if (!v.valid) return jsonError(res, 400, v.error);

    const result = await getPageInfo({ tabId: body.tab });
    jsonSuccess(res, result);
  },

  // Click
  'POST /click': async (req, res, body) => {
    const v = validateSession();
    if (!v.valid) return jsonError(res, 400, v.error);

    await click({
      tabId: body.tab,
      ref: body.ref,
      text: body.text,
      selector: body.selector,
      exact: body.exact,
      doubleClick: body.doubleClick || body.double,
      button: body.button,
      modifiers: body.modifiers,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, { clicked: body.ref || body.text || body.selector });
  },

  // Type
  'POST /type': async (req, res, body) => {
    const v = validateSession();
    if (!v.valid) return jsonError(res, 400, v.error);

    await type({
      tabId: body.tab,
      ref: body.ref,
      textContent: body.textContent,
      selector: body.selector,
      exact: body.exact,
      text: body.text,
      submit: body.submit,
      slowly: body.slowly,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, { typed: body.text, ref: body.ref || body.textContent || body.selector });
  },

  // Press key
  'POST /press': async (req, res, body) => {
    const v = validateSession();
    if (!v.valid) return jsonError(res, 400, v.error);

    await pressKey({
      tabId: body.tab,
      key: body.key,
      delayMs: body.delay,
    });
    jsonSuccess(res, { pressed: body.key });
  },

  // Wait
  'POST /wait': async (req, res, body) => {
    const v = validateSession();
    if (!v.valid) return jsonError(res, 400, v.error);

    await waitFor({
      tabId: body.tab,
      timeMs: body.time,
      text: body.text,
      textGone: body.textGone,
      selector: body.selector,
      url: body.url,
      loadState: body.loadState,
      fn: body.fn,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, { waited: true });
  },

  // Evaluate JavaScript
  'POST /evaluate': async (req, res, body) => {
    const v = validateSession();
    if (!v.valid) return jsonError(res, 400, v.error);

    const result = await evaluate({
      tabId: body.tab,
      fn: body.fn || body.js || body.code,
      ref: body.ref,
    });
    jsonSuccess(res, { result });
  },

  // Screenshot
  'POST /screenshot': async (req, res, body) => {
    const v = validateSession();
    if (!v.valid) return jsonError(res, 400, v.error);

    const { buffer } = await takeScreenshot({
      tabId: body.tab,
      ref: body.ref,
      element: body.element,
      fullPage: body.fullPage,
      type: body.type || 'png',
    });

    jsonSuccess(res, {
      screenshot: buffer.toString('base64'),
      type: body.type || 'png',
    });
  },

  // Get text content
  'POST /text': async (req, res, body) => {
    const v = validateSession();
    if (!v.valid) return jsonError(res, 400, v.error);

    const text = await getTextContent({
      tabId: body.tab,
      selector: body.selector,
    });
    jsonSuccess(res, { text });
  },

  // List tabs
  'POST /tabs': async (req, res) => {
    const v = validateSession();
    if (!v.valid) return jsonError(res, 400, v.error);

    const tabs = await listTabsAsync();
    jsonSuccess(res, { tabs });
  },

  // Open new tab
  'POST /tabs/open': async (req, res, body) => {
    const v = validateSession();
    if (!v.valid) return jsonError(res, 400, v.error);

    const tab = await openTab(body.url);
    jsonSuccess(res, { tab });
  },

  // Close tab
  'POST /tabs/close': async (req, res, body) => {
    const v = validateSession();
    if (!v.valid) return jsonError(res, 400, v.error);

    const tabId = body.tab || body.tabId;
    if (!tabId) return jsonError(res, 400, 'tab is required');

    const result = await closeTab(tabId);
    jsonSuccess(res, result);
  },

  // Focus tab
  'POST /tabs/focus': async (req, res, body) => {
    const v = validateSession();
    if (!v.valid) return jsonError(res, 400, v.error);

    const tabId = body.tab || body.tabId;
    if (!tabId) return jsonError(res, 400, 'tab is required');

    const result = await focusTab(tabId);
    jsonSuccess(res, result);
  },

  // Get/Set mode
  'GET /mode': async (req, res) => {
    jsonSuccess(res, { mode: getCurrentMode() });
  },

  'POST /mode': async (req, res, body) => {
    try {
      setCurrentMode(body.mode);
      jsonSuccess(res, { mode: getCurrentMode() });
    } catch (err) {
      jsonError(res, 400, err.message);
    }
  },
};

// ---------------------------------------------------------------------------
// HTTP Server
// ---------------------------------------------------------------------------

async function handleRequest(req, res) {
  const { pathname } = parseUrl(req.url, true);
  const method = req.method.toUpperCase();
  const routeKey = `${method} ${pathname}`;

  const handler = routes[routeKey];
  if (!handler) {
    return jsonError(res, 404, `Unknown route: ${routeKey}`);
  }

  try {
    const body = method === 'GET' ? {} : await parseBody(req);
    await handler(req, res, body);
  } catch (err) {
    console.error(`[ERROR] ${routeKey}:`, err.message);
    jsonError(res, 500, err.message);
  }
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------

const args = process.argv.slice(2);
let daemonPort = DEFAULT_DAEMON_PORT;

for (let i = 0; i < args.length; i++) {
  if (args[i] === '--port' && args[i + 1]) {
    daemonPort = parseInt(args[i + 1], 10);
    i++;
  }
}

const server = createServer(handleRequest);
actualDaemonPort = daemonPort;

server.listen(daemonPort, '127.0.0.1', () => {
  console.log(`[cc-fox-browser] Daemon listening on http://127.0.0.1:${daemonPort}`);
  console.log('[cc-fox-browser] Engine: Camoufox (anti-detection Firefox)');
  writeLockfile(daemonPort);
  console.log('[cc-fox-browser] Ready for commands');
});

// Graceful shutdown
process.on('SIGINT', async () => {
  console.log('\n[cc-fox-browser] Shutting down...');
  removeLockfile();
  await stopBrowser();
  server.close();
  process.exit(0);
});

process.on('SIGTERM', async () => {
  console.log('\n[cc-fox-browser] Shutting down...');
  removeLockfile();
  await stopBrowser();
  server.close();
  process.exit(0);
});
