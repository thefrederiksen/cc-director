#!/usr/bin/env node
// CC Browser v2 - CLI Client
// Usage: cc-browser [--connection <name>] <command> [options]

import { spawn } from 'child_process';
import { dirname, join } from 'path';
import { fileURLToPath } from 'url';
import { readFileSync, existsSync } from 'fs';
import { homedir } from 'os';

const __dirname = dirname(fileURLToPath(import.meta.url));

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

const DEFAULT_DAEMON_PORT = 9280;

function getLockfilePath() {
  const localAppData = process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local');
  return join(localAppData, 'cc-browser', 'daemon.lock');
}

function readLockfile() {
  const lockPath = getLockfilePath();
  if (!existsSync(lockPath)) return null;
  try {
    return JSON.parse(readFileSync(lockPath, 'utf8'));
  } catch {
    return null;
  }
}

function getDaemonPort() {
  const lock = readLockfile();
  return lock?.port || DEFAULT_DAEMON_PORT;
}

function getDaemonUrl() {
  return `http://127.0.0.1:${getDaemonPort()}`;
}

// ---------------------------------------------------------------------------
// Argument Parsing
// ---------------------------------------------------------------------------

function parseArgs(argv) {
  const args = argv.slice(2);
  let connectionName = null;
  let commandArgs = [];

  // Extract global --connection flag
  for (let i = 0; i < args.length; i++) {
    if ((args[i] === '--connection' || args[i] === '-c') && args[i + 1]) {
      connectionName = args[i + 1];
      i++; // skip value
    } else {
      commandArgs.push(args[i]);
    }
  }

  const command = commandArgs[0] || '';
  const rest = commandArgs.slice(1);

  return { connectionName, command, rest };
}

function parseFlags(args) {
  const flags = {};
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (arg.startsWith('--')) {
      const key = arg.slice(2);
      const next = args[i + 1];
      if (next && !next.startsWith('--')) {
        flags[key] = next;
        i++;
      } else {
        flags[key] = true;
      }
    } else if (!flags._positional) {
      flags._positional = arg;
    }
  }
  return flags;
}

// ---------------------------------------------------------------------------
// HTTP Client
// ---------------------------------------------------------------------------

async function daemonRequest(method, path, body = null) {
  const url = `${getDaemonUrl()}${path}`;
  const options = {
    method,
    headers: { 'Content-Type': 'application/json' },
    signal: AbortSignal.timeout(60000),
  };

  if (body) {
    options.body = JSON.stringify(body);
  }

  const res = await fetch(url, options);
  const data = await res.json();

  if (!data.success) {
    throw new Error(data.error || `Daemon returned ${res.status}`);
  }

  return data;
}

async function post(path, body) {
  return daemonRequest('POST', path, body);
}

async function get(path) {
  return daemonRequest('GET', path);
}

// ---------------------------------------------------------------------------
// Daemon Management
// ---------------------------------------------------------------------------

async function isDaemonRunning() {
  try {
    await get('/');
    return true;
  } catch {
    return false;
  }
}

async function ensureDaemon() {
  if (await isDaemonRunning()) return;

  console.log('[cc-browser] Starting daemon...');
  const daemonPath = join(__dirname, 'daemon.mjs');
  const child = spawn(process.execPath, [daemonPath], {
    detached: true,
    stdio: 'ignore',
  });
  child.unref();

  // Wait for daemon to start
  for (let i = 0; i < 20; i++) {
    await new Promise(r => setTimeout(r, 250));
    if (await isDaemonRunning()) {
      console.log('[cc-browser] Daemon started');
      return;
    }
  }
  throw new Error('Daemon failed to start within 5 seconds');
}

// ---------------------------------------------------------------------------
// Command Handlers
// ---------------------------------------------------------------------------

async function cmdConnections(rest) {
  const subcommand = rest[0] || 'list';
  const flags = parseFlags(rest.slice(1));

  switch (subcommand) {
    case 'list': {
      const data = await get('/connections');
      if (!data.connections || data.connections.length === 0) {
        console.log('No connections configured.');
        console.log('');
        console.log('Create one with:');
        console.log('  cc-browser connections add <name> --url <url> [--tool <tool>]');
        return;
      }
      console.log('Connections:');
      for (const c of data.connections) {
        const status = c.connected ? '[CONNECTED]' : '[disconnected]';
        const tool = c.toolBinding ? ` (tool: ${c.toolBinding})` : '';
        console.log(`  ${c.name} ${status}${tool}`);
        if (c.url) console.log(`    url: ${c.url}`);
      }
      break;
    }

    case 'add': {
      const name = flags._positional || rest[1];
      if (!name) {
        console.error('Usage: cc-browser connections add <name> [--url <url>] [--tool <tool>]');
        process.exit(1);
      }
      const data = await post('/connections/add', {
        name,
        url: flags.url,
        tool: flags.tool,
        browser: flags.browser,
      });
      console.log(`Connection "${data.connection.name}" created.`);
      break;
    }

    case 'open': {
      const name = flags._positional || rest[1];
      if (!name) {
        console.error('Usage: cc-browser connections open <name>');
        process.exit(1);
      }
      const data = await post('/connections/open', {
        name,
        url: flags.url,
      });
      console.log(`Opening "${name}" (${data.browserKind}, pid: ${data.pid})`);
      console.log(`Extension will connect automatically.`);
      break;
    }

    case 'close': {
      const name = flags._positional || rest[1];
      if (!name) {
        console.error('Usage: cc-browser connections close <name>');
        process.exit(1);
      }
      await post('/connections/close', { name });
      console.log(`Connection "${name}" closed.`);
      break;
    }

    case 'remove': {
      const name = flags._positional || rest[1];
      if (!name) {
        console.error('Usage: cc-browser connections remove <name>');
        process.exit(1);
      }
      await post('/connections/remove', { name });
      console.log(`Connection "${name}" removed.`);
      break;
    }

    case 'status': {
      const data = await get('/');
      console.log(`Daemon: running (port ${data.daemonPort})`);
      console.log(`Active connections: ${data.activeConnections?.length || 0}`);
      if (data.connections) {
        for (const c of data.connections) {
          const status = c.connected ? 'CONNECTED' : 'disconnected';
          console.log(`  ${c.name}: ${status}`);
        }
      }
      break;
    }

    default:
      console.error(`Unknown connections subcommand: ${subcommand}`);
      console.error('Available: list, add, open, close, remove, status');
      process.exit(1);
  }
}

async function cmdBrowserAction(command, connectionName, rest) {
  const flags = parseFlags(rest);
  const body = { connection: connectionName };

  // Map CLI flags to body params
  switch (command) {
    case 'navigate':
      body.url = flags.url || flags._positional;
      if (!body.url) {
        console.error('Usage: cc-browser navigate --url <url>');
        process.exit(1);
      }
      break;

    case 'snapshot':
      if (flags.interactive) body.interactive = true;
      if (flags.compact) body.compact = true;
      if (flags.maxDepth) body.maxDepth = parseInt(flags.maxDepth, 10);
      if (flags.maxChars) body.maxChars = parseInt(flags.maxChars, 10);
      break;

    case 'click':
      body.ref = flags.ref || flags._positional;
      body.text = flags.text;
      body.selector = flags.selector;
      body.exact = flags.exact;
      body.doubleClick = flags.double;
      break;

    case 'type':
      body.ref = flags.ref;
      body.text = flags.text || flags._positional;
      body.selector = flags.selector;
      body.submit = flags.submit;
      break;

    case 'fill':
      body.ref = flags.ref;
      body.value = flags.value || flags.text || flags._positional;
      body.selector = flags.selector;
      break;

    case 'press':
      body.key = flags.key || flags._positional;
      break;

    case 'hover':
      body.ref = flags.ref || flags._positional;
      body.text = flags.text;
      body.selector = flags.selector;
      break;

    case 'drag':
      body.startRef = flags.from;
      body.endRef = flags.to;
      break;

    case 'select':
      body.ref = flags.ref;
      body.value = flags.value || flags._positional;
      break;

    case 'scroll':
      body.direction = flags.direction || flags._positional || 'down';
      body.amount = flags.amount ? parseInt(flags.amount, 10) : undefined;
      body.ref = flags.ref;
      break;

    case 'wait':
      body.text = flags.text;
      body.textGone = flags.textGone;
      body.selector = flags.selector;
      body.url = flags.url;
      body.time = flags.time ? parseInt(flags.time, 10) : undefined;
      break;

    case 'evaluate':
      body.fn = flags.fn || flags.js || flags._positional;
      body.ref = flags.ref;
      break;

    case 'screenshot':
      body.type = flags.type || 'jpeg';
      body.quality = flags.quality ? parseInt(flags.quality, 10) : 80;
      break;

    case 'tabs':
      break;

    case 'tabs/open':
      body.url = flags.url || flags._positional;
      break;

    case 'tabs/close':
      body.tab = flags.tab || flags._positional;
      break;

    case 'tabs/focus':
      body.tab = flags.tab || flags._positional;
      break;

    case 'text':
      body.selector = flags.selector;
      body.ref = flags.ref;
      break;

    case 'html':
      body.selector = flags.selector;
      body.ref = flags.ref;
      body.outer = flags.outer;
      break;

    case 'info':
      break;

    default:
      console.error(`Unknown command: ${command}`);
      process.exit(1);
  }

  const data = await post(`/${command}`, body);

  // Format output
  if (command === 'snapshot') {
    console.log(data.snapshot);
    if (data.stats) {
      console.error(`--- ${data.stats.refs} refs, ${data.stats.interactive} interactive, ${data.stats.chars} chars ---`);
    }
  } else if (command === 'screenshot') {
    // Write base64 to stdout (CLI consumers pipe to file)
    process.stdout.write(data.screenshot || '');
  } else if (command === 'tabs') {
    const tabs = data.tabs || data;
    if (Array.isArray(tabs)) {
      for (const tab of tabs) {
        console.log(`  [${tab.tabId}] ${tab.title || '(no title)'} - ${tab.url}`);
      }
    }
  } else {
    // Generic JSON output
    const { success, ...rest } = data;
    const output = Object.keys(rest).length > 0 ? rest : data;
    console.log(JSON.stringify(output, null, 2));
  }
}

// ---------------------------------------------------------------------------
// Usage
// ---------------------------------------------------------------------------

function printUsage() {
  console.log('CC Browser v2 - Chrome Extension + Native Messaging');
  console.log('');
  console.log('Usage: cc-browser [--connection <name>] <command> [options]');
  console.log('');
  console.log('Connection Management:');
  console.log('  connections list                   List all connections');
  console.log('  connections add <name> [--url URL] [--tool TOOL]');
  console.log('  connections open <name>            Launch Chrome for connection');
  console.log('  connections close <name>           Close Chrome for connection');
  console.log('  connections remove <name>          Delete connection');
  console.log('  connections status                 Show daemon and connection status');
  console.log('');
  console.log('Browser Commands (require --connection or single active connection):');
  console.log('  navigate --url <url>               Navigate to URL');
  console.log('  snapshot [--interactive] [--compact]');
  console.log('  click --ref <ref>                  Click element');
  console.log('  type --ref <ref> --text "..."       Type text');
  console.log('  fill --ref <ref> --value "..."      Fill input');
  console.log('  press --key Enter                  Press key');
  console.log('  hover --ref <ref>                  Hover element');
  console.log('  scroll [--direction down] [--amount 500]');
  console.log('  wait --text "..." | --selector "..."');
  console.log('  evaluate --fn "() => document.title"');
  console.log('  screenshot [--type jpeg]');
  console.log('  tabs                               List tabs');
  console.log('  tabs/open [--url URL]              Open new tab');
  console.log('  tabs/close --tab <id>              Close tab');
  console.log('  info                               Page URL, title, viewport');
  console.log('  text [--selector "..."]            Get text content');
  console.log('  html [--selector "..."]            Get HTML content');
  console.log('');
  console.log('Daemon:');
  console.log('  daemon                             Start daemon in foreground');
  console.log('  status                             Show daemon status');
  console.log('  install                            Install native messaging host');
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------

async function main() {
  const { connectionName, command, rest } = parseArgs(process.argv);

  if (!command || command === 'help' || command === '--help' || command === '-h') {
    printUsage();
    process.exit(0);
  }

  // Commands that don't need the daemon
  if (command === 'install') {
    const installPath = join(__dirname, '..', 'native-host', 'install.mjs');
    const { default: run } = await import(installPath);
    return;
  }

  if (command === 'daemon') {
    // Run daemon in foreground
    const daemonPath = join(__dirname, 'daemon.mjs');
    const flags = parseFlags(rest);
    const args = [daemonPath];
    if (flags.port) args.push('--port', flags.port);
    const child = spawn(process.execPath, args, { stdio: 'inherit' });
    child.on('exit', (code) => process.exit(code || 0));
    return;
  }

  // All other commands need the daemon running
  await ensureDaemon();

  if (command === 'connections') {
    await cmdConnections(rest);
    return;
  }

  if (command === 'status') {
    await cmdConnections(['status']);
    return;
  }

  // Browser commands
  const browserCommands = [
    'navigate', 'snapshot', 'click', 'type', 'fill', 'press',
    'hover', 'drag', 'select', 'scroll', 'wait', 'evaluate',
    'screenshot', 'tabs', 'tabs/open', 'tabs/close', 'tabs/focus',
    'text', 'html', 'info', 'upload', 'resize',
  ];

  if (browserCommands.includes(command)) {
    await cmdBrowserAction(command, connectionName, rest);
    return;
  }

  console.error(`Unknown command: ${command}`);
  console.error('Run "cc-browser help" for usage.');
  process.exit(1);
}

main().catch((err) => {
  console.error(`[cc-browser] ERROR: ${err.message}`);
  process.exit(1);
});
