#!/usr/bin/env node
// CC Fox Browser - CLI Client
// Camoufox browser automation for Claude Code
// Usage: cc-fox-browser <command> [options]

import { spawn } from 'child_process';
import { dirname, join } from 'path';
import { fileURLToPath } from 'url';
import { readFileSync, existsSync } from 'fs';

const __dirname = dirname(fileURLToPath(import.meta.url));

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

const DEFAULT_DAEMON_PORT = 9380;

function getLockfilePath() {
  const localAppData = process.env.LOCALAPPDATA || join(process.env.HOME || '', 'AppData', 'Local');
  return join(localAppData, 'cc-fox-browser', 'daemon.lock');
}

function readLockfile() {
  const lockPath = getLockfilePath();
  if (!existsSync(lockPath)) {
    return null;
  }
  try {
    const content = readFileSync(lockPath, 'utf8');
    return JSON.parse(content);
  } catch {
    return null;
  }
}

function getDaemonPort(args) {
  if (args.port) return args.port;

  const lockData = readLockfile();
  if (lockData && lockData.port) {
    return lockData.port;
  }

  return DEFAULT_DAEMON_PORT;
}

// ---------------------------------------------------------------------------
// Argument Parser
// ---------------------------------------------------------------------------

function parseArgs(argv) {
  const args = { _: [] };
  const tokens = argv.slice(2);

  for (let i = 0; i < tokens.length; i++) {
    const token = tokens[i];
    if (token.startsWith('--')) {
      const key = token.slice(2);
      const next = tokens[i + 1];
      if (next && !next.startsWith('--')) {
        try {
          args[key] = JSON.parse(next);
        } catch {
          args[key] = next;
        }
        i++;
      } else {
        args[key] = true;
      }
    } else {
      args._.push(token);
    }
  }

  return args;
}

// ---------------------------------------------------------------------------
// HTTP Client
// ---------------------------------------------------------------------------

async function request(method, path, body, port = DEFAULT_DAEMON_PORT, timeoutMs = 60000) {
  const url = `http://127.0.0.1:${port}${path}`;

  try {
    const res = await fetch(url, {
      method,
      headers: { 'Content-Type': 'application/json' },
      body: body ? JSON.stringify(body) : undefined,
      signal: AbortSignal.timeout(timeoutMs),
    });

    const data = await res.json();
    return data;
  } catch (err) {
    if (err.code === 'ECONNREFUSED' || err.cause?.code === 'ECONNREFUSED') {
      return {
        success: false,
        error: `Daemon not running on port ${port}. Start it with: cc-fox-browser daemon`,
      };
    }
    throw err;
  }
}

// ---------------------------------------------------------------------------
// Output Helpers
// ---------------------------------------------------------------------------

function output(data) {
  console.log(JSON.stringify(data, null, 2));
}

function outputError(message) {
  console.log(JSON.stringify({ success: false, error: message }, null, 2));
}

// ---------------------------------------------------------------------------
// Command Handlers
// ---------------------------------------------------------------------------

const commands = {
  // Start daemon in foreground
  daemon: async (args) => {
    const port = args.port || DEFAULT_DAEMON_PORT;
    const daemonScript = join(__dirname, 'daemon.mjs');

    const daemonArgs = ['--port', String(port)];

    console.error(`[cc-fox-browser] Starting daemon on port ${port}...`);

    const child = spawn('node', [daemonScript, ...daemonArgs], {
      stdio: 'inherit',
      cwd: join(__dirname, '..'),
    });

    child.on('exit', (code) => {
      process.exit(code || 0);
    });
  },

  // Check status
  status: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('GET', '/', null, port);
    output(result);
  },

  // Start browser
  start: async (args) => {
    const port = getDaemonPort(args);
    const body = {};
    if (args.workspace) body.workspace = args.workspace;
    if (args.headless) body.headless = true;
    if (args.mode) body.mode = args.mode;

    const result = await request('POST', '/start', body, port);
    output(result);
  },

  // Stop browser
  stop: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/stop', {}, port);
    output(result);
  },

  // Navigate
  navigate: async (args) => {
    const port = getDaemonPort(args);
    const body = {
      url: args.url || args._[1],
      tab: args.tab,
      waitUntil: args.waitUntil,
      timeout: args.timeout,
    };
    const result = await request('POST', '/navigate', body, port);
    output(result);
  },

  // Reload
  reload: async (args) => {
    const port = getDaemonPort(args);
    const body = { tab: args.tab, waitUntil: args.waitUntil, timeout: args.timeout };
    const result = await request('POST', '/reload', body, port);
    output(result);
  },

  // Back
  back: async (args) => {
    const port = getDaemonPort(args);
    const body = { tab: args.tab };
    const result = await request('POST', '/back', body, port);
    output(result);
  },

  // Forward
  forward: async (args) => {
    const port = getDaemonPort(args);
    const body = { tab: args.tab };
    const result = await request('POST', '/forward', body, port);
    output(result);
  },

  // Snapshot
  snapshot: async (args) => {
    const port = getDaemonPort(args);
    const body = {
      tab: args.tab,
      interactive: args.interactive,
      compact: args.compact,
      maxDepth: args.maxDepth,
      maxChars: args.maxChars,
    };
    const result = await request('POST', '/snapshot', body, port);
    output(result);
  },

  // Page info
  info: async (args) => {
    const port = getDaemonPort(args);
    const body = { tab: args.tab };
    const result = await request('POST', '/info', body, port);
    output(result);
  },

  // Click
  click: async (args) => {
    const port = getDaemonPort(args);
    const body = {
      tab: args.tab,
      ref: args.ref || args._[1],
      text: args.text,
      selector: args.selector,
      exact: args.exact,
      doubleClick: args.double || args.doubleClick,
      button: args.button,
      timeout: args.timeout,
    };
    const result = await request('POST', '/click', body, port);
    output(result);
  },

  // Type
  type: async (args) => {
    const port = getDaemonPort(args);
    const body = {
      tab: args.tab,
      ref: args.ref || args._[1],
      text: args.text || args._[2],
      textContent: args.textContent,
      selector: args.selector,
      exact: args.exact,
      submit: args.submit,
      slowly: args.slowly,
      timeout: args.timeout,
    };
    const result = await request('POST', '/type', body, port);
    output(result);
  },

  // Press key
  press: async (args) => {
    const port = getDaemonPort(args);
    const body = {
      tab: args.tab,
      key: args.key || args._[1],
      delay: args.delay,
    };
    const result = await request('POST', '/press', body, port);
    output(result);
  },

  // Wait
  wait: async (args) => {
    const port = getDaemonPort(args);
    const body = {
      tab: args.tab,
      time: args.time,
      text: args.text,
      textGone: args.textGone,
      selector: args.selector,
      url: args.url,
      loadState: args.loadState,
      fn: args.fn,
      timeout: args.timeout,
    };
    const result = await request('POST', '/wait', body, port);
    output(result);
  },

  // Evaluate JavaScript
  evaluate: async (args) => {
    const port = getDaemonPort(args);
    const body = {
      tab: args.tab,
      fn: args.fn || args.js || args.code || args._[1],
      ref: args.ref,
    };
    const result = await request('POST', '/evaluate', body, port);
    output(result);
  },

  // Screenshot
  screenshot: async (args) => {
    const port = getDaemonPort(args);
    const body = {
      tab: args.tab,
      ref: args.ref,
      element: args.element,
      fullPage: args.fullPage,
      type: args.type || 'png',
    };
    const result = await request('POST', '/screenshot', body, port);
    output(result);
  },

  // Text content
  text: async (args) => {
    const port = getDaemonPort(args);
    const body = { tab: args.tab, selector: args.selector };
    const result = await request('POST', '/text', body, port);
    output(result);
  },

  // List tabs
  tabs: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/tabs', {}, port);
    output(result);
  },

  // Open tab
  'tabs-open': async (args) => {
    const port = getDaemonPort(args);
    const body = { url: args.url || args._[1] };
    const result = await request('POST', '/tabs/open', body, port);
    output(result);
  },

  // Close tab
  'tabs-close': async (args) => {
    const port = getDaemonPort(args);
    const body = { tab: args.tab || args._[1] };
    const result = await request('POST', '/tabs/close', body, port);
    output(result);
  },

  // Focus tab
  'tabs-focus': async (args) => {
    const port = getDaemonPort(args);
    const body = { tab: args.tab || args._[1] };
    const result = await request('POST', '/tabs/focus', body, port);
    output(result);
  },

  // Help
  help: async () => {
    const helpText = `cc-fox-browser - Camoufox browser automation (anti-detection Firefox)

USAGE:
  cc-fox-browser <command> [options]

LIFECYCLE:
  daemon                     Start daemon in foreground
  status                     Check daemon and browser status
  start [--workspace NAME]   Launch Camoufox browser
  stop                       Stop browser

NAVIGATION:
  navigate --url URL         Navigate to URL
  reload                     Reload current page
  back                       Go back
  forward                    Go forward

INSPECTION:
  snapshot [--interactive]    Get DOM snapshot with element refs
  info                       Get page URL, title, viewport
  text [--selector SEL]      Get page text content
  screenshot                 Take screenshot (base64)

INTERACTIONS:
  click --ref REF            Click element by ref
  type --ref REF --text TXT  Type text into element
  press --key KEY            Press keyboard key
  wait [--text TXT]          Wait for condition
  evaluate --fn CODE         Run JavaScript

TABS:
  tabs                       List open tabs
  tabs-open [--url URL]      Open new tab
  tabs-close --tab ID        Close tab
  tabs-focus --tab ID        Switch to tab

OPTIONS:
  --port PORT                Daemon port (default: 9380)
  --workspace NAME           Workspace for persistent profile
  --tab ID                   Target tab (default: active tab)
  --mode fast|human          Interaction mode (default: human)

EXAMPLES:
  cc-fox-browser daemon
  cc-fox-browser start --workspace upwork
  cc-fox-browser navigate --url https://www.upwork.com
  cc-fox-browser snapshot --interactive
  cc-fox-browser click --ref e5
  cc-fox-browser type --ref e3 --text "hello"
  cc-fox-browser stop
`;
    console.log(helpText);
  },
};

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------

const args = parseArgs(process.argv);
const command = args._[0] || 'help';

const handler = commands[command];
if (!handler) {
  outputError(`Unknown command: ${command}. Run "cc-fox-browser help" for usage.`);
  process.exit(1);
}

handler(args).catch((err) => {
  outputError(err.message);
  process.exit(1);
});
