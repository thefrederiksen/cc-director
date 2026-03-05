#!/usr/bin/env node
// CC Browser v2 - Native Messaging Host
// Bridges Chrome Extension (stdin/stdout, 4-byte LE prefix JSON)
// to the cc-browser daemon (WebSocket).

import { readFileSync, existsSync, readdirSync } from 'fs';
import { join } from 'path';
import { homedir } from 'os';
import { execSync } from 'child_process';
import { WebSocket } from 'ws';

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

const DEFAULT_DAEMON_PORT = 9280;
const RECONNECT_DELAY_MS = 2000;
const MAX_MESSAGE_SIZE = 1024 * 1024; // 1MB native messaging limit

// ---------------------------------------------------------------------------
// Lockfile
// ---------------------------------------------------------------------------

function getLockfilePath() {
  const localAppData = process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local');
  return join(localAppData, 'cc-browser', 'daemon.lock');
}

function readDaemonPort() {
  const lockPath = getLockfilePath();
  if (!existsSync(lockPath)) return DEFAULT_DAEMON_PORT;

  try {
    const data = JSON.parse(readFileSync(lockPath, 'utf8'));
    return data.port || DEFAULT_DAEMON_PORT;
  } catch {
    return DEFAULT_DAEMON_PORT;
  }
}

// ---------------------------------------------------------------------------
// stdin/stdout Native Messaging Protocol
// 4-byte little-endian length prefix + JSON payload
// ---------------------------------------------------------------------------

function readNativeMessage(callback) {
  let buffer = Buffer.alloc(0);

  process.stdin.on('data', (chunk) => {
    buffer = Buffer.concat([buffer, chunk]);

    while (buffer.length >= 4) {
      const messageLength = buffer.readUInt32LE(0);

      if (messageLength > MAX_MESSAGE_SIZE) {
        writeNativeMessage({
          id: 'error',
          error: `Message too large: ${messageLength} bytes (max: ${MAX_MESSAGE_SIZE})`,
        });
        buffer = Buffer.alloc(0);
        return;
      }

      if (buffer.length < 4 + messageLength) {
        // Not enough data yet, wait for more
        return;
      }

      const jsonBytes = buffer.subarray(4, 4 + messageLength);
      buffer = buffer.subarray(4 + messageLength);

      try {
        const msg = JSON.parse(jsonBytes.toString('utf8'));
        callback(msg);
      } catch (err) {
        writeNativeMessage({
          id: 'error',
          error: `Invalid JSON from extension: ${err.message}`,
        });
      }
    }
  });
}

function writeNativeMessage(msg) {
  const json = JSON.stringify(msg);
  const jsonBytes = Buffer.from(json, 'utf8');

  if (jsonBytes.length > MAX_MESSAGE_SIZE) {
    const errorMsg = JSON.stringify({
      id: msg.id || 'error',
      error: `Response too large: ${jsonBytes.length} bytes (max: ${MAX_MESSAGE_SIZE})`,
    });
    const errorBytes = Buffer.from(errorMsg, 'utf8');
    const header = Buffer.alloc(4);
    header.writeUInt32LE(errorBytes.length, 0);
    process.stdout.write(Buffer.concat([header, errorBytes]));
    return;
  }

  const header = Buffer.alloc(4);
  header.writeUInt32LE(jsonBytes.length, 0);
  process.stdout.write(Buffer.concat([header, jsonBytes]));
}

// ---------------------------------------------------------------------------
// Connection Name Resolution
// ---------------------------------------------------------------------------

function getConnectionsDir() {
  const localAppData = process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local');
  return join(localAppData, 'cc-director', 'connections');
}

function getAncestorPids() {
  // Build a full process tree, then walk from our PID upward
  if (process.platform !== 'win32') return [process.pid];

  try {
    const output = execSync(
      'powershell -NoProfile -Command "Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId | ConvertTo-Json"',
      { encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'], timeout: 5000 }
    );

    const procs = JSON.parse(output);
    const parentMap = new Map();
    for (const p of procs) {
      parentMap.set(p.ProcessId, p.ParentProcessId);
    }

    const ancestors = [];
    let current = process.pid;
    const seen = new Set();
    while (current && !seen.has(current)) {
      seen.add(current);
      ancestors.push(current);
      current = parentMap.get(current);
    }
    return ancestors;
  } catch (err) {
    log(`Failed to get ancestor PIDs: ${err.message}`);
    return [process.pid];
  }
}

function resolveConnectionName() {
  // 1. Environment variable override (for testing)
  if (process.env.CC_BROWSER_CONNECTION) {
    return process.env.CC_BROWSER_CONNECTION;
  }

  // 2. Walk ancestor PIDs and match against cc-browser.json configs
  const connectionsDir = getConnectionsDir();
  if (!existsSync(connectionsDir)) return 'default';

  const ancestorPids = getAncestorPids();
  const ancestorSet = new Set(ancestorPids);

  try {
    const entries = readdirSync(connectionsDir, { withFileTypes: true });
    for (const entry of entries) {
      if (!entry.isDirectory()) continue;
      const configPath = join(connectionsDir, entry.name, 'cc-browser.json');
      if (!existsSync(configPath)) continue;

      try {
        const config = JSON.parse(readFileSync(configPath, 'utf8'));
        if (config.chromePid && ancestorSet.has(config.chromePid)) {
          log(`Resolved connection "${config.connection}" via PID ${config.chromePid}`);
          return config.connection;
        }
      } catch {
        // Skip malformed config files
      }
    }
  } catch {
    // Connections dir not readable
  }

  log('WARNING: Could not resolve connection name, using "default"');
  return 'default';
}

// ---------------------------------------------------------------------------
// WebSocket Connection to Daemon
// ---------------------------------------------------------------------------

let ws = null;
let wsConnected = false;
const connectionName = resolveConnectionName();

function connectWebSocket() {
  const port = readDaemonPort();
  const url = `ws://127.0.0.1:${port}/ws?connection=${encodeURIComponent(connectionName)}`;

  try {
    ws = new WebSocket(url);

    ws.on('open', () => {
      wsConnected = true;
      log(`WebSocket connected to daemon on port ${port} (connection: ${connectionName})`);
    });

    ws.on('message', (data) => {
      try {
        const msg = JSON.parse(data.toString());
        // Forward daemon messages to extension via stdout
        writeNativeMessage(msg);
      } catch (err) {
        log(`Invalid JSON from daemon: ${err.message}`);
      }
    });

    ws.on('close', () => {
      wsConnected = false;
      log('WebSocket disconnected from daemon');
      setTimeout(() => connectWebSocket(), RECONNECT_DELAY_MS);
    });

    ws.on('error', (err) => {
      log(`WebSocket error: ${err.message}`);
      // Close will fire after error, triggering reconnect
    });
  } catch (err) {
    log(`Failed to connect WebSocket: ${err.message}`);
    setTimeout(() => connectWebSocket(), RECONNECT_DELAY_MS);
  }
}

// ---------------------------------------------------------------------------
// Message Routing
// ---------------------------------------------------------------------------

function handleExtensionMessage(msg) {
  if (!wsConnected || !ws) {
    writeNativeMessage({
      id: msg.id || 'error',
      error: 'Not connected to daemon',
    });
    return;
  }

  try {
    ws.send(JSON.stringify(msg));
  } catch (err) {
    writeNativeMessage({
      id: msg.id || 'error',
      error: `Failed to send to daemon: ${err.message}`,
    });
  }
}

// ---------------------------------------------------------------------------
// Logging (stderr only - stdout is reserved for native messaging protocol)
// ---------------------------------------------------------------------------

function log(msg) {
  process.stderr.write(`[native-host] ${msg}\n`);
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------

log(`Starting native host (connection: ${connectionName})`);

// Start reading from extension
readNativeMessage(handleExtensionMessage);

// Connect to daemon
connectWebSocket();

// Keep alive
process.stdin.on('end', () => {
  log('Extension closed stdin, shutting down');
  if (ws) ws.close();
  process.exit(0);
});

process.on('SIGTERM', () => {
  log('SIGTERM received, shutting down');
  if (ws) ws.close();
  process.exit(0);
});
