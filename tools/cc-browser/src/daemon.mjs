#!/usr/bin/env node
// CC Browser v2 - HTTP Daemon Server
// Routes commands through Chrome Extension via WebSocket transport.
// Usage: node daemon.mjs [--port 9280]

import { createServer } from 'http';
import { parse as parseUrl } from 'url';
import { existsSync, writeFileSync, unlinkSync, mkdirSync } from 'fs';
import { join } from 'path';
import { homedir } from 'os';

import { Transport } from './transport.mjs';
import {
  listConnections, getConnection, createConnection,
  deleteConnection, setConnectionStatus, getProfileDir,
} from './connections.mjs';
import {
  launchChromeForConnection, killChromeForConnection,
  findChromeExecutable, listAvailableBrowsers,
} from './chrome-launch.mjs';
import { ensureInstalled } from '../native-host/install.mjs';
import {
  resolveSkill, listAllSkills, listManagedSkills, getManagedSkill,
  forkSkill, resetSkill, appendLearnedPattern, getLearnedPatterns,
  clearLearnedPatterns,
} from './skills.mjs';

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

const DEFAULT_DAEMON_PORT = 9280;

// ---------------------------------------------------------------------------
// Lockfile
// ---------------------------------------------------------------------------

function getLockfilePath() {
  const localAppData = process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local');
  return join(localAppData, 'cc-browser', 'daemon.lock');
}

function writeLockfile(port) {
  const lockPath = getLockfilePath();
  const lockDir = join(lockPath, '..');
  if (!existsSync(lockDir)) mkdirSync(lockDir, { recursive: true });
  writeFileSync(lockPath, JSON.stringify({
    port,
    pid: process.pid,
    startedAt: new Date().toISOString(),
    version: 2,
  }, null, 2));
  console.log(`[cc-browser] Lockfile written: ${lockPath}`);
}

function removeLockfile() {
  const lockPath = getLockfilePath();
  if (existsSync(lockPath)) {
    unlinkSync(lockPath);
    console.log(`[cc-browser] Lockfile removed`);
  }
}

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
      if (body.length > 1024 * 1024) reject(new Error('Request body too large'));
    });
    req.on('end', () => {
      try {
        resolve(body ? JSON.parse(body) : {});
      } catch (err) {
        reject(new Error(`Invalid JSON: ${err.message}`));
      }
    });
    req.on('error', reject);
  });
}

// ---------------------------------------------------------------------------
// Connection Locking
// ---------------------------------------------------------------------------

// In-memory lock map: connectionName -> { owner, acquiredAt, expiresAt }
const connectionLocks = new Map();
const DEFAULT_LOCK_TTL_MS = 5 * 60 * 1000; // 5 minutes

function acquireLock(name, owner, ttlMs = DEFAULT_LOCK_TTL_MS) {
  // Clean expired locks first
  const existing = connectionLocks.get(name);
  if (existing && Date.now() < existing.expiresAt) {
    if (existing.owner === owner) {
      // Same owner - extend the lock
      existing.expiresAt = Date.now() + ttlMs;
      existing.acquiredAt = new Date().toISOString();
      console.log(`[cc-browser] Lock extended: ${name} by ${owner}`);
      return { extended: true };
    }
    // Different owner - reject immediately
    const err = new Error(`Connection "${name}" is locked by "${existing.owner}" (acquired ${existing.acquiredAt}). Try again later.`);
    err.statusCode = 409;
    err.lockedBy = existing.owner;
    throw err;
  }

  // Lock is free or expired
  connectionLocks.set(name, {
    owner,
    acquiredAt: new Date().toISOString(),
    expiresAt: Date.now() + ttlMs,
  });
  console.log(`[cc-browser] Lock acquired: ${name} by ${owner} (ttl=${ttlMs}ms)`);
  return { acquired: true };
}

function releaseLock(name, owner) {
  const existing = connectionLocks.get(name);
  if (!existing) return { released: false, reason: 'no lock held' };

  if (existing.owner !== owner) {
    return { released: false, reason: `lock held by "${existing.owner}", not "${owner}"` };
  }

  connectionLocks.delete(name);
  console.log(`[cc-browser] Lock released: ${name} by ${owner}`);
  return { released: true };
}

function renewLock(name, owner, ttlMs = DEFAULT_LOCK_TTL_MS) {
  const existing = connectionLocks.get(name);
  if (!existing || existing.owner !== owner) {
    const err = new Error(`Cannot renew: lock on "${name}" not held by "${owner}"`);
    err.statusCode = 409;
    throw err;
  }
  existing.expiresAt = Date.now() + ttlMs;
  return { renewed: true };
}

function getLockInfo(name) {
  const lock = connectionLocks.get(name);
  if (!lock) return null;
  if (Date.now() >= lock.expiresAt) {
    connectionLocks.delete(name);
    return null;
  }
  return { ...lock, expiresAt: new Date(lock.expiresAt).toISOString() };
}

// ---------------------------------------------------------------------------
// Connection Resolution
// ---------------------------------------------------------------------------

function resolveConnection(body) {
  const name = body.connection;
  if (!name) {
    // Try to use the only active connection
    const active = transport.listConnections();
    if (active.length === 1) return active[0];
    if (active.length === 0) {
      throw new Error('No connection specified and no active connections. Use --connection or open a connection first.');
    }
    throw new Error(`Multiple active connections: ${active.join(', ')}. Specify --connection.`);
  }
  return name;
}

function requireConnected(body) {
  const name = resolveConnection(body);
  if (!transport.isConnected(name)) {
    throw new Error(`Connection "${name}" is not connected. Open it first with: cc-browser connections open ${name}`);
  }

  // Check lock - if locked by someone else, reject
  const lock = getLockInfo(name);
  if (lock && body.connection && body.owner && lock.owner !== body.owner) {
    const err = new Error(`Connection "${name}" is locked by "${lock.owner}". Try again later.`);
    err.statusCode = 409;
    throw err;
  }

  return name;
}

// ---------------------------------------------------------------------------
// Transport
// ---------------------------------------------------------------------------

const transport = new Transport();

// ---------------------------------------------------------------------------
// Route Handlers
// ---------------------------------------------------------------------------

const routes = {
  // Status
  'GET /': async (req, res) => {
    const connections = listConnections();
    const activeNames = transport.listConnections();

    jsonSuccess(res, {
      daemon: 'running',
      version: 2,
      daemonPort: actualDaemonPort,
      connections: connections.map(c => ({
        ...c,
        connected: activeNames.includes(c.name),
        lock: getLockInfo(c.name),
      })),
      activeConnections: activeNames,
    });
  },

  // List browsers
  'GET /browsers': async (req, res) => {
    jsonSuccess(res, { browsers: listAvailableBrowsers() });
  },

  // --- Connection Management ---

  'GET /connections': async (req, res) => {
    const connections = listConnections();
    const activeNames = transport.listConnections();
    jsonSuccess(res, {
      connections: connections.map(c => ({
        ...c,
        connected: activeNames.includes(c.name),
        lock: getLockInfo(c.name),
      })),
    });
  },

  'POST /connections/add': async (req, res, body) => {
    const conn = createConnection({
      name: body.name,
      url: body.url,
      toolBinding: body.tool || body.toolBinding,
      browser: body.browser,
    });
    jsonSuccess(res, { connection: conn });
  },

  'POST /connections/open': async (req, res, body) => {
    const name = body.name || body.connection;
    if (!name) return jsonError(res, 400, 'name is required');

    const conn = getConnection(name);
    if (!conn) return jsonError(res, 404, `Connection "${name}" not found`);

    const profileDir = getProfileDir(name);
    const result = await launchChromeForConnection(name, profileDir, {
      browser: conn.browser,
      url: body.url || conn.url,
    });

    setConnectionStatus(name, 'connecting');

    // Resolve navigation skill for this connection
    const skill = resolveSkill(name);

    jsonSuccess(res, {
      connection: name,
      ...result,
      status: 'connecting',
      skill: skill.content ? {
        type: skill.type,
        content: skill.content,
        learnedPatterns: skill.learnedPatterns,
      } : null,
    });
  },

  'POST /connections/acquire': async (req, res, body) => {
    const name = body.name || body.connection;
    const owner = body.owner;
    if (!name) return jsonError(res, 400, 'name is required');
    if (!owner) return jsonError(res, 400, 'owner is required (e.g., "cc-reddit")');

    const ttl = body.ttl || DEFAULT_LOCK_TTL_MS;
    const result = acquireLock(name, owner, ttl);
    jsonSuccess(res, { connection: name, owner, ...result });
  },

  'POST /connections/release': async (req, res, body) => {
    const name = body.name || body.connection;
    const owner = body.owner;
    if (!name) return jsonError(res, 400, 'name is required');
    if (!owner) return jsonError(res, 400, 'owner is required');

    const result = releaseLock(name, owner);
    jsonSuccess(res, { connection: name, ...result });
  },

  'POST /connections/renew': async (req, res, body) => {
    const name = body.name || body.connection;
    const owner = body.owner;
    if (!name) return jsonError(res, 400, 'name is required');
    if (!owner) return jsonError(res, 400, 'owner is required');

    const ttl = body.ttl || DEFAULT_LOCK_TTL_MS;
    const result = renewLock(name, owner, ttl);
    jsonSuccess(res, { connection: name, ...result });
  },

  'POST /connections/close': async (req, res, body) => {
    const name = body.name || body.connection;
    if (!name) return jsonError(res, 400, 'name is required');

    const conn = getConnection(name);
    if (!conn) return jsonError(res, 404, `Connection "${name}" not found`);

    // Release any lock on this connection
    const lock = getLockInfo(name);
    if (lock) {
      releaseLock(name, lock.owner);
    }

    // Close WebSocket connection
    transport.closeConnection(name);

    // Kill Chrome process
    const profileDir = getProfileDir(name);
    const result = await killChromeForConnection(profileDir);

    setConnectionStatus(name, 'disconnected');

    jsonSuccess(res, { connection: name, ...result });
  },

  'POST /connections/remove': async (req, res, body) => {
    const name = body.name || body.connection;
    if (!name) return jsonError(res, 400, 'name is required');

    // Close first if connected
    transport.closeConnection(name);
    deleteConnection(name);

    jsonSuccess(res, { removed: name });
  },

  // --- Skills ---

  'GET /skills': async (req, res) => {
    const skills = listAllSkills();
    jsonSuccess(res, skills);
  },

  'GET /skills/managed': async (req, res) => {
    const skills = listManagedSkills();
    jsonSuccess(res, { skills });
  },

  'POST /skills/show': async (req, res, body) => {
    const name = body.name || body.connection;
    if (!name) return jsonError(res, 400, 'name is required');

    if (body.managed) {
      const content = getManagedSkill(name);
      if (!content) return jsonError(res, 404, `No managed skill "${name}"`);
      jsonSuccess(res, { type: 'managed', name, content });
    } else {
      const skill = resolveSkill(name);
      jsonSuccess(res, { name, ...skill });
    }
  },

  'POST /skills/fork': async (req, res, body) => {
    const name = body.name || body.connection;
    if (!name) return jsonError(res, 400, 'name is required');
    const path = forkSkill(name);
    jsonSuccess(res, { connection: name, customSkillPath: path });
  },

  'POST /skills/reset': async (req, res, body) => {
    const name = body.name || body.connection;
    if (!name) return jsonError(res, 400, 'name is required');
    resetSkill(name);
    jsonSuccess(res, { connection: name, reset: true });
  },

  'POST /skills/learn': async (req, res, body) => {
    const name = body.name || body.connection;
    const pattern = body.pattern;
    if (!name) return jsonError(res, 400, 'name is required');
    if (!pattern) return jsonError(res, 400, 'pattern is required');
    appendLearnedPattern(name, pattern);
    jsonSuccess(res, { connection: name, learned: true });
  },

  'POST /skills/learned': async (req, res, body) => {
    const name = body.name || body.connection;
    if (!name) return jsonError(res, 400, 'name is required');
    const patterns = getLearnedPatterns(name);
    jsonSuccess(res, { connection: name, patterns });
  },

  'POST /skills/clear-learned': async (req, res, body) => {
    const name = body.name || body.connection;
    if (!name) return jsonError(res, 400, 'name is required');
    const cleared = clearLearnedPatterns(name);
    jsonSuccess(res, { connection: name, cleared });
  },

  // --- Browser Commands (forwarded to extension via transport) ---

  'POST /navigate': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'navigate', {
      url: body.url,
      tabId: body.tab || body.tabId,
      timeout: body.timeout,
    });
    jsonSuccess(res, result);
  },

  'POST /snapshot': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'snapshot', {
      interactive: body.interactive,
      compact: body.compact,
      maxDepth: body.maxDepth,
      maxChars: body.maxChars,
      tabId: body.tab || body.tabId,
    });
    jsonSuccess(res, result);
  },

  'POST /click': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'click', {
      ref: body.ref, text: body.text, selector: body.selector,
      exact: body.exact, doubleClick: body.doubleClick || body.double,
      tabId: body.tab || body.tabId,
    });
    jsonSuccess(res, result);
  },

  'POST /type': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'type', {
      ref: body.ref, text: body.text, selector: body.selector,
      exact: body.exact, submit: body.submit,
      tabId: body.tab || body.tabId,
    });
    jsonSuccess(res, result);
  },

  'POST /fill': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'fill', {
      ref: body.ref, text: body.text, value: body.value,
      selector: body.selector, tabId: body.tab || body.tabId,
    });
    jsonSuccess(res, result);
  },

  'POST /press': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'press', {
      key: body.key, ref: body.ref,
      tabId: body.tab || body.tabId,
    });
    jsonSuccess(res, result);
  },

  'POST /hover': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'hover', {
      ref: body.ref, text: body.text, selector: body.selector,
      exact: body.exact, tabId: body.tab || body.tabId,
    });
    jsonSuccess(res, result);
  },

  'POST /drag': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'drag', {
      startRef: body.startRef || body.from,
      endRef: body.endRef || body.to,
      tabId: body.tab || body.tabId,
    });
    jsonSuccess(res, result);
  },

  'POST /select': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'select', {
      ref: body.ref,
      value: body.value, values: body.values,
      tabId: body.tab || body.tabId,
    });
    jsonSuccess(res, result);
  },

  'POST /scroll': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'scroll', {
      direction: body.direction, amount: body.amount,
      ref: body.ref, tabId: body.tab || body.tabId,
    });
    jsonSuccess(res, result);
  },

  'POST /wait': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'wait', {
      time: body.time, text: body.text, textGone: body.textGone,
      selector: body.selector, url: body.url,
      tabId: body.tab || body.tabId,
      timeout: body.timeout,
    });
    jsonSuccess(res, result);
  },

  'POST /evaluate': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'evaluate', {
      fn: body.fn || body.js || body.code,
      ref: body.ref, tabId: body.tab || body.tabId,
    });
    jsonSuccess(res, result);
  },

  'POST /screenshot': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'screenshot', {
      tabId: body.tab || body.tabId,
      type: body.type || 'jpeg',
      quality: body.quality || 80,
    });
    jsonSuccess(res, result);
  },

  'POST /upload': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'upload', {
      ref: body.ref, selector: body.selector,
      data: body.data, filename: body.filename,
      mimeType: body.mimeType,
      tabId: body.tab || body.tabId,
    });
    jsonSuccess(res, result);
  },

  'POST /tabs': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'tabs', {});
    jsonSuccess(res, { tabs: result });
  },

  'POST /tabs/open': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'tabs.open', {
      url: body.url, active: body.active,
    });
    jsonSuccess(res, { tab: result });
  },

  'POST /tabs/close': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'tabs.close', {
      tabId: body.tab || body.tabId,
    });
    jsonSuccess(res, result);
  },

  'POST /tabs/focus': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'tabs.focus', {
      tabId: body.tab || body.tabId,
    });
    jsonSuccess(res, result);
  },

  'POST /text': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'getText', {
      ref: body.ref, selector: body.selector,
      tabId: body.tab || body.tabId,
    });
    jsonSuccess(res, result);
  },

  'POST /html': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'getHtml', {
      ref: body.ref, selector: body.selector, outer: body.outer,
      tabId: body.tab || body.tabId,
    });
    jsonSuccess(res, result);
  },

  'POST /info': async (req, res, body) => {
    const conn = requireConnected(body);
    const result = await transport.sendCommand(conn, 'getInfo', {
      tabId: body.tab || body.tabId,
    });
    jsonSuccess(res, result);
  },

  'POST /resize': async (req, res, body) => {
    // Resize is handled via Chrome API, not content script
    jsonError(res, 501, 'Resize not yet implemented in v2');
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
    const status = err.statusCode || 500;
    console.error(`[ERROR] ${routeKey}: ${err.message}`);
    jsonError(res, status, err.message);
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

let actualDaemonPort = daemonPort;

const server = createServer(handleRequest);

// Attach WebSocket transport to the HTTP server
transport.attach(server);

server.listen(daemonPort, '127.0.0.1', () => {
  console.log(`[cc-browser] Daemon v2 listening on http://127.0.0.1:${daemonPort}`);
  console.log(`[cc-browser] WebSocket transport ready on ws://127.0.0.1:${daemonPort}/ws`);
  writeLockfile(daemonPort);

  // Ensure native messaging host is installed (no-op if already correct)
  try {
    ensureInstalled();
  } catch (err) {
    console.error(`[cc-browser] WARNING: Native host install check failed: ${err.message}`);
  }

  console.log('[cc-browser] Ready for commands');
});

// Graceful shutdown
function shutdown() {
  console.log('\n[cc-browser] Shutting down...');
  removeLockfile();
  transport.close();
  server.close();
  process.exit(0);
}

process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
