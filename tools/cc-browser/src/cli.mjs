#!/usr/bin/env node
// CC Browser v2 - CLI Client
// Usage: cc-browser [--connection <name>] <command> [options]

import { spawn } from 'child_process';
import { dirname, join } from 'path';
import { fileURLToPath } from 'url';
import { readFileSync, writeFileSync, existsSync } from 'fs';
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
        console.log('  cc-browser connections add <name> --url <url> [--tool <tool>] [--skill-name <skill>]');
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
        console.error('Usage: cc-browser connections add <name> [--url <url>] [--tool <tool>] [--skill-name <skill>]');
        process.exit(1);
      }
      const data = await post('/connections/add', {
        name,
        url: flags.url,
        tool: flags.tool,
        browser: flags.browser,
        skillName: flags['skill-name'] || flags.skillName || null,
      });
      console.log(`Connection "${data.connection.name}" created.`);
      break;
    }

    case 'open': {
      const name = flags._positional || rest[1];
      if (!name) {
        console.error('Usage: cc-browser connections open <name> [--background]');
        process.exit(1);
      }
      const data = await post('/connections/open', {
        name,
        url: flags.url,
        background: flags.background || false,
      });
      console.log(`Opening "${name}" (${data.browserKind}, pid: ${data.pid})`);
      if (flags.background) console.log('Running in background (minimized).');
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

async function cmdSkills(rest) {
  const subcommand = rest[0] || 'list';
  const flags = parseFlags(rest.slice(1));

  switch (subcommand) {
    case 'list': {
      const data = await get('/skills');
      console.log('Managed skills:');
      if (data.managed && data.managed.length > 0) {
        for (const s of data.managed) {
          console.log(`  ${s.name} (${s.site}) v${s.version}`);
        }
      } else {
        console.log('  (none)');
      }
      console.log('');
      console.log('Custom skills (per-connection):');
      if (data.custom && data.custom.length > 0) {
        for (const s of data.custom) {
          console.log(`  ${s.name}`);
        }
      } else {
        console.log('  (none)');
      }
      break;
    }

    case 'show': {
      const name = flags._positional || rest[1];
      if (!name) {
        console.error('Usage: cc-browser skills show <connection> [--managed]');
        process.exit(1);
      }
      const data = await post('/skills/show', { name, managed: flags.managed || false });
      console.log(`Skill for "${name}" (${data.type}):`);
      console.log('');
      if (data.content) {
        console.log(data.content);
      } else {
        console.log('(no skill found)');
      }
      if (data.learnedPatterns) {
        console.log('');
        console.log('--- Learned Patterns ---');
        console.log(data.learnedPatterns);
      }
      break;
    }

    case 'fork': {
      const name = flags._positional || rest[1];
      if (!name) {
        console.error('Usage: cc-browser skills fork <connection>');
        process.exit(1);
      }
      const data = await post('/skills/fork', { name });
      console.log(`Forked managed skill to custom: ${data.customSkillPath}`);
      break;
    }

    case 'reset': {
      const name = flags._positional || rest[1];
      if (!name) {
        console.error('Usage: cc-browser skills reset <connection>');
        process.exit(1);
      }
      await post('/skills/reset', { name });
      console.log(`Custom skill for "${name}" removed. Now using managed skill.`);
      break;
    }

    case 'learn': {
      const name = flags._positional || rest[1];
      const pattern = flags.pattern || rest.slice(2).join(' ');
      if (!name || !pattern) {
        console.error('Usage: cc-browser skills learn <connection> "pattern description"');
        process.exit(1);
      }
      await post('/skills/learn', { name, pattern });
      console.log(`Learned pattern appended for "${name}".`);
      break;
    }

    case 'learned': {
      const name = flags._positional || rest[1];
      if (!name) {
        console.error('Usage: cc-browser skills learned <connection>');
        process.exit(1);
      }
      const data = await post('/skills/learned', { name });
      if (data.patterns) {
        console.log(data.patterns);
      } else {
        console.log(`No learned patterns for "${name}".`);
      }
      break;
    }

    case 'clear-learned': {
      const name = flags._positional || rest[1];
      if (!name) {
        console.error('Usage: cc-browser skills clear-learned <connection>');
        process.exit(1);
      }
      const data = await post('/skills/clear-learned', { name });
      if (data.cleared) {
        console.log(`Learned patterns cleared for "${name}".`);
      } else {
        console.log(`No learned patterns to clear for "${name}".`);
      }
      break;
    }

    default:
      console.error(`Unknown skills subcommand: ${subcommand}`);
      console.error('Available: list, show, fork, reset, learn, learned, clear-learned');
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
      if (flags.selector) body.selector = flags.selector;
      if (flags['no-limit']) body.maxChars = 0;
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
      body.selector = flags.selector;
      body.findText = flags['find-text'];
      body.text = flags.text || flags._positional;
      body.submit = flags.submit;
      break;

    case 'fill': {
      body.ref = flags.ref;
      const rawValue = flags.value || flags.text || flags._positional;
      // Convert literal \n sequences to actual newlines so CLI callers
      // don't need special shell quoting (e.g. $'...' syntax)
      body.value = rawValue ? rawValue.replace(/\\n/g, '\n') : rawValue;
      body.selector = flags.selector;
      break;
    }

    case 'press':
      body.key = flags.key || flags._positional;
      body.ref = flags.ref;
      body.selector = flags.selector;
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
      body.selector = flags.selector;
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
      body.type = flags.type || 'png';
      body.quality = flags.quality ? parseInt(flags.quality, 10) : 80;
      body._output = flags.output || flags.o;
      break;

    case 'back':
    case 'forward':
      body.tabId = flags.tab || flags.tabId;
      break;

    case 'reload':
      body.tabId = flags.tab || flags.tabId;
      body.timeout = flags.timeout ? parseInt(flags.timeout, 10) : undefined;
      break;

    case 'links':
      body.selector = flags.selector;
      body.pattern = flags.pattern || flags._positional;
      body.unique = flags.unique !== 'false';
      body.includeAttrs = flags.attrs || false;
      break;

    case 'waitNetworkIdle':
      body.idleTime = flags.idleTime ? parseInt(flags.idleTime, 10) : undefined;
      body.timeout = flags.timeout ? parseInt(flags.timeout, 10) : undefined;
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
    const base64 = data.screenshot || '';
    const ext = body.type === 'jpeg' ? 'jpg' : 'png';
    const outputPath = body._output || `screenshot-${Date.now()}.${ext}`;
    const buffer = Buffer.from(base64, 'base64');
    writeFileSync(outputPath, buffer);
    console.log(`Saved: ${outputPath} (${buffer.length} bytes)`);
  } else if (command === 'tabs') {
    const tabs = data.tabs || data;
    if (Array.isArray(tabs)) {
      for (const tab of tabs) {
        console.log(`  [${tab.tabId}] ${tab.title || '(no title)'} - ${tab.url}`);
      }
    }
  } else if (command === 'links') {
    const links = data.links || [];
    console.log(`Found ${links.length} links (${data.total} total on page):`);
    for (const link of links) {
      const text = link.text ? ` "${link.text}"` : '';
      console.log(`  ${link.href}${text}`);
    }
  } else {
    // Generic JSON output
    const { success, ...rest } = data;
    const output = Object.keys(rest).length > 0 ? rest : data;
    console.log(JSON.stringify(output, null, 2));
  }
}

// ---------------------------------------------------------------------------
// Cookies Command
// ---------------------------------------------------------------------------

async function cmdCookies(connectionName, rest) {
  const subcommand = rest[0] || 'list';
  const flags = parseFlags(rest.slice(1));
  const body = { connection: connectionName };

  switch (subcommand) {
    case 'list': {
      body.domain = flags.domain;
      body.url = flags.url;
      body.name = flags.name;
      const data = await post('/cookies/list', body);
      const cookies = data.cookies || [];
      if (cookies.length === 0) {
        console.log('No cookies found.');
      } else {
        console.log(`Cookies (${cookies.length}):`);
        for (const c of cookies) {
          console.log(`  ${c.domain} | ${c.name} = ${c.value.slice(0, 60)}${c.value.length > 60 ? '...' : ''}`);
        }
      }
      break;
    }

    case 'set': {
      if (!flags.url || !flags.name) {
        console.error('Usage: cc-browser cookies set --url <url> --name <name> --value <value> [--domain <d>] [--path <p>]');
        process.exit(1);
      }
      body.url = flags.url;
      body.name = flags.name;
      body.value = flags.value || '';
      body.domain = flags.domain;
      body.path = flags.path;
      body.secure = flags.secure || false;
      body.httpOnly = flags.httpOnly || false;
      const data = await post('/cookies/set', body);
      console.log(`Cookie set: ${data.name} on ${data.domain}`);
      break;
    }

    case 'delete': {
      if (!flags.url || !flags.name) {
        console.error('Usage: cc-browser cookies delete --url <url> --name <name>');
        process.exit(1);
      }
      body.url = flags.url;
      body.name = flags.name;
      await post('/cookies/delete', body);
      console.log(`Cookie deleted: ${flags.name}`);
      break;
    }

    case 'export': {
      body.domain = flags.domain;
      body.url = flags.url;
      const data = await post('/cookies/export', body);
      console.log(JSON.stringify(data.cookies, null, 2));
      console.log(`--- ${data.count} cookies exported ---`);
      break;
    }

    default:
      console.error(`Unknown cookies subcommand: ${subcommand}`);
      console.error('Available: list, set, delete, export');
      process.exit(1);
  }
}

// ---------------------------------------------------------------------------
// Batch Command
// ---------------------------------------------------------------------------

async function cmdBatch(connectionName, rest) {
  const flags = parseFlags(rest);
  const input = flags._positional;

  if (!input) {
    console.error('Usage: cc-browser batch <json-file-or-inline-json> [--stop-on-error]');
    console.error('');
    console.error('JSON format: [{"command": "navigate", "params": {"url": "..."}}, ...]');
    process.exit(1);
  }

  let commands;
  if (existsSync(input)) {
    commands = JSON.parse(readFileSync(input, 'utf8'));
  } else {
    commands = JSON.parse(input);
  }

  if (!Array.isArray(commands)) {
    console.error('Batch input must be a JSON array of {command, params} objects.');
    process.exit(1);
  }

  const body = {
    connection: connectionName,
    commands,
    stopOnError: flags['stop-on-error'] || false,
  };

  const data = await post('/batch', body);
  console.log(`Batch completed: ${data.count} commands`);
  for (const r of data.results) {
    if (r.error) {
      console.log(`  [X] ${r.command}: ${r.error}`);
    } else {
      console.log(`  [+] ${r.command}: OK`);
    }
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
  console.log('  connections add <name> [--url URL] [--tool TOOL] [--skill-name SKILL]');
  console.log('  connections open <name>            Launch Chrome for connection');
  console.log('  connections close <name>           Close Chrome for connection');
  console.log('  connections remove <name>          Delete connection');
  console.log('  connections status                 Show daemon and connection status');
  console.log('');
  console.log('Browser Commands (require --connection or single active connection):');
  console.log('  navigate --url <url>               Navigate to URL');
  console.log('  back                               Go back in history');
  console.log('  forward                            Go forward in history');
  console.log('  reload                             Reload current page');
  console.log('  snapshot [--interactive] [--compact] [--selector "css"] [--maxChars N] [--no-limit]');
  console.log('  click --ref <ref> | --selector "css" | --text "..."');
  console.log('  type --ref <ref> | --selector "css" --text "..."');
  console.log('  fill --ref <ref> | --selector "css" --value "..."');
  console.log('  press --key Enter [--ref <ref> | --selector "css"]');
  console.log('  hover --ref <ref> | --selector "css"');
  console.log('  scroll [--direction down] [--amount 500]');
  console.log('  wait --text "..." | --selector "..."');
  console.log('  waitNetworkIdle [--idleTime 500] [--timeout 30000]');
  console.log('  evaluate --fn "() => document.title"');
  console.log('  screenshot [--output file.png] [--type png|jpeg]');
  console.log('  links [--pattern "regex"] [--selector "a.nav"] [--attrs]');
  console.log('  tabs                               List tabs');
  console.log('  tabs/open [--url URL]              Open new tab');
  console.log('  tabs/close --tab <id>              Close tab');
  console.log('  info                               Page URL, title, viewport');
  console.log('  text [--selector "..."]            Get text content');
  console.log('  html [--selector "..."]            Get HTML content');
  console.log('');
  console.log('Cookie Management:');
  console.log('  cookies list [--domain <d>] [--url <u>]');
  console.log('  cookies set --url <url> --name <n> --value <v>');
  console.log('  cookies delete --url <url> --name <n>');
  console.log('  cookies export [--domain <d>]');
  console.log('');
  console.log('Batch and History:');
  console.log('  batch <json-file-or-json> [--stop-on-error]');
  console.log('  history                            Show recent action log');
  console.log('');
  console.log('Navigation Skills:');
  console.log('  skills list                        List all skills (managed + custom)');
  console.log('  skills show <connection>           Show resolved skill for connection');
  console.log('  skills show <name> --managed       Show a managed skill by name');
  console.log('  skills fork <connection>           Fork managed skill to custom');
  console.log('  skills reset <connection>          Reset to managed skill');
  console.log('  skills learn <connection> "text"   Append learned pattern');
  console.log('  skills learned <connection>        Show learned patterns');
  console.log('  skills clear-learned <connection>  Clear learned patterns');
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

  if (command === 'skills') {
    await cmdSkills(rest);
    return;
  }

  if (command === 'status') {
    await cmdConnections(['status']);
    return;
  }

  // Cookies subcommand
  if (command === 'cookies') {
    await cmdCookies(connectionName, rest);
    return;
  }

  // Batch command
  if (command === 'batch') {
    await cmdBatch(connectionName, rest);
    return;
  }

  // History command
  if (command === 'history') {
    const data = await get('/history');
    const actions = data.actions || [];
    if (actions.length === 0) {
      console.log('No actions in history.');
    } else {
      console.log(`Action history (${actions.length} of ${data.total}):`);
      for (const a of actions) {
        const conn = a.connection ? ` [${a.connection}]` : '';
        console.log(`  ${a.timestamp} ${a.command}${conn} (${a.elapsedMs}ms)`);
      }
    }
    return;
  }

  // Browser commands
  const browserCommands = [
    'navigate', 'snapshot', 'click', 'type', 'fill', 'press',
    'hover', 'drag', 'select', 'scroll', 'wait', 'evaluate',
    'screenshot', 'tabs', 'tabs/open', 'tabs/close', 'tabs/focus',
    'text', 'html', 'info', 'upload', 'resize',
    'back', 'forward', 'reload', 'links', 'waitNetworkIdle',
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
