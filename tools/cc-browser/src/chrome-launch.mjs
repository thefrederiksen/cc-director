// CC Browser v2 - Chrome Launcher
// Launches Chrome with --load-extension for native messaging.
// No Playwright, no CDP, no --enable-automation.

import { spawn } from 'child_process';
import { existsSync, mkdirSync, rmSync, writeFileSync, readFileSync } from 'fs';
import { rm } from 'fs/promises';
import { join, resolve } from 'path';
import { homedir } from 'os';
import { fileURLToPath } from 'url';
import { dirname } from 'path';

const __dirname = dirname(fileURLToPath(import.meta.url));

// ---------------------------------------------------------------------------
// Chrome Executable Detection (ported from cc-browser-archived/src/chrome.mjs)
// ---------------------------------------------------------------------------

// Brave is preferred because Chrome stable blocks --load-extension.
// Brave is Chromium-based (open source) and supports --load-extension.
const WINDOWS_CHROME_PATHS = [
  process.env.LOCALAPPDATA && join(process.env.LOCALAPPDATA, 'BraveSoftware', 'Brave-Browser', 'Application', 'brave.exe'),
  process.env['ProgramFiles'] && join(process.env['ProgramFiles'], 'BraveSoftware', 'Brave-Browser', 'Application', 'brave.exe'),
  process.env['ProgramFiles(x86)'] && join(process.env['ProgramFiles(x86)'], 'BraveSoftware', 'Brave-Browser', 'Application', 'brave.exe'),
  process.env.LOCALAPPDATA && join(process.env.LOCALAPPDATA, 'Google', 'Chrome', 'Application', 'chrome.exe'),
  process.env['ProgramFiles'] && join(process.env['ProgramFiles'], 'Google', 'Chrome', 'Application', 'chrome.exe'),
  process.env['ProgramFiles(x86)'] && join(process.env['ProgramFiles(x86)'], 'Google', 'Chrome', 'Application', 'chrome.exe'),
  process.env.LOCALAPPDATA && join(process.env.LOCALAPPDATA, 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
  process.env['ProgramFiles'] && join(process.env['ProgramFiles'], 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
  process.env['ProgramFiles(x86)'] && join(process.env['ProgramFiles(x86)'], 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
].filter(Boolean);

const MACOS_CHROME_PATHS = [
  '/Applications/Brave Browser.app/Contents/MacOS/Brave Browser',
  '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
  '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
];

const LINUX_CHROME_PATHS = [
  '/usr/bin/brave-browser',
  '/usr/bin/google-chrome',
  '/usr/bin/google-chrome-stable',
  '/usr/bin/chromium',
  '/usr/bin/chromium-browser',
  '/usr/bin/microsoft-edge',
];

export function findChromeExecutable(preferredBrowser = null) {
  const platform = process.platform;
  let candidates;

  if (platform === 'win32') candidates = WINDOWS_CHROME_PATHS;
  else if (platform === 'darwin') candidates = MACOS_CHROME_PATHS;
  else candidates = LINUX_CHROME_PATHS;

  // "chrome" is treated as "auto" (use default priority order: Brave > Chrome > Edge).
  // Only "edge" or "brave" are treated as explicit overrides.
  if (preferredBrowser && preferredBrowser.toLowerCase() !== 'chrome') {
    const pref = preferredBrowser.toLowerCase();
    const patterns = {
      edge: ['msedge.exe', 'microsoft edge', 'microsoft-edge'],
      brave: ['brave.exe', 'brave browser', 'brave-browser'],
    };

    const match = patterns[pref];
    if (match) {
      for (const path of candidates) {
        if (path) {
          const lower = path.toLowerCase();
          if (match.some(p => lower.includes(p)) && existsSync(path)) {
            return { path, kind: pref };
          }
        }
      }
      throw new Error(`Browser "${preferredBrowser}" not found`);
    }
  }

  for (const path of candidates) {
    if (path && existsSync(path)) {
      const kind = path.toLowerCase().includes('edge') ? 'edge'
        : path.toLowerCase().includes('brave') ? 'brave' : 'chrome';
      return { path, kind };
    }
  }

  return null;
}

export function listAvailableBrowsers() {
  const platform = process.platform;
  let candidates;

  if (platform === 'win32') candidates = WINDOWS_CHROME_PATHS;
  else if (platform === 'darwin') candidates = MACOS_CHROME_PATHS;
  else candidates = LINUX_CHROME_PATHS;

  const available = [];
  for (const path of candidates) {
    if (path && existsSync(path)) {
      const kind = path.toLowerCase().includes('edge') ? 'edge'
        : path.toLowerCase().includes('brave') ? 'brave' : 'chrome';
      available.push({ kind, path });
    }
  }
  return available;
}

// ---------------------------------------------------------------------------
// User Data Dir Cleanup
// ---------------------------------------------------------------------------

const CLEANUP_TARGETS = [
  'GrShaderCache', 'GraphiteDawnCache', 'lockfile',
  'SingletonLock', 'SingletonSocket', 'SingletonCookie',
];

export async function cleanUserDataDir(userDataDir) {
  if (!userDataDir || !existsSync(userDataDir)) return;
  for (const target of CLEANUP_TARGETS) {
    try {
      await rm(join(userDataDir, target), { recursive: true, force: true });
    } catch {
      // Target did not exist
    }
  }
}

// ---------------------------------------------------------------------------
// Profile Preparation (run before each launch, while browser is NOT running)
// ---------------------------------------------------------------------------

function prepareProfileForLaunch(profileDir) {
  const defaultDir = join(profileDir, 'Default');
  if (!existsSync(defaultDir)) {
    mkdirSync(defaultDir, { recursive: true });
  }

  const prefsPath = join(defaultDir, 'Preferences');
  let prefs = {};
  if (existsSync(prefsPath)) {
    try {
      prefs = JSON.parse(readFileSync(prefsPath, 'utf8'));
    } catch {
      prefs = {};
    }
  }

  let changed = false;

  // Enable developer mode so --load-extension works
  if (!prefs.extensions) prefs.extensions = {};
  if (!prefs.extensions.ui) prefs.extensions.ui = {};
  if (prefs.extensions.ui.developer_mode !== true) {
    prefs.extensions.ui.developer_mode = true;
    changed = true;
  }

  // Clear crash state so browser starts normally and trusts persisted cookies
  if (!prefs.profile) prefs.profile = {};
  if (prefs.profile.exit_type !== 'Normal') {
    prefs.profile.exit_type = 'Normal';
    prefs.profile.exited_cleanly = true;
    changed = true;
  }

  if (changed) {
    writeFileSync(prefsPath, JSON.stringify(prefs, null, 2), 'utf8');
    console.log('[chrome-launch] Profile prepared (developer mode, clean exit state)');
  }

  // Delete session restore files so Chrome starts with a clean tab slate.
  // Chrome stores open tabs in these files and restores them on next launch,
  // causing tab accumulation over time. We pass the URL as a launch arg instead.
  const sessionTargets = [
    join(defaultDir, 'Sessions'),
    join(defaultDir, 'Current Session'),
    join(defaultDir, 'Current Tabs'),
    join(defaultDir, 'Last Session'),
    join(defaultDir, 'Last Tabs'),
  ];
  for (const target of sessionTargets) {
    if (existsSync(target)) {
      try {
        rmSync(target, { recursive: true, force: true });
      } catch {
        // File may be locked if browser is still shutting down
      }
    }
  }
}

// ---------------------------------------------------------------------------
// Chrome Launch
// ---------------------------------------------------------------------------

export function getExtensionDir() {
  return resolve(join(__dirname, '..', 'extension'));
}

export async function launchChromeForConnection(name, profileDir, opts = {}) {
  const {
    browser: preferredBrowser,
    url,
    background,
  } = opts;

  // Find Chrome executable
  const detected = findChromeExecutable(preferredBrowser);
  if (!detected) {
    throw new Error('Chrome/Edge/Brave not found. Install Chrome or specify a browser.');
  }

  const { path: chromePath, kind: browserKind } = detected;

  // Ensure profile directory exists
  if (!existsSync(profileDir)) {
    mkdirSync(profileDir, { recursive: true });
  }

  // Clean corruption-prone files
  await cleanUserDataDir(profileDir);

  // Prepare profile: developer mode, session restore, clear crash state
  prepareProfileForLaunch(profileDir);

  // Extension directory
  const extensionDir = getExtensionDir();
  if (!existsSync(join(extensionDir, 'manifest.json'))) {
    throw new Error(`Extension not found at: ${extensionDir}`);
  }

  // Build launch args - NO --enable-automation, NO --remote-debugging-port
  // NOTE: --load-extension is blocked on Chrome stable (Chrome 130+).
  // We use Brave (Chromium-based, open source) which supports --load-extension.
  const args = [
    `--user-data-dir=${profileDir}`,
    `--load-extension=${extensionDir}`,
    `--disable-extensions-except=${extensionDir}`,
    '--no-first-run',
    '--no-default-browser-check',
    '--disable-features=TranslateUI',
    '--disable-sync',
  ];

  // Edge-specific flags
  if (browserKind === 'edge') {
    args.push(
      '--disable-features=msEdgeSingleSignOn,msEdgeWorkspacesIntegration',
      '--no-service-autorun',
      '--disable-background-mode',
    );
  }

  // Start minimized if background mode requested
  if (background) {
    args.push('--start-minimized');
  }

  // Open URL if specified
  if (url) {
    args.push(url);
  }

  console.log(`[chrome-launch] Launching ${browserKind}: ${chromePath}`);
  console.log(`[chrome-launch] Profile: ${profileDir}`);
  console.log(`[chrome-launch] Extension: ${extensionDir}`);

  // Write connection config BEFORE launch so the native host can resolve it
  const configPath = join(profileDir, 'cc-browser.json');
  writeFileSync(configPath, JSON.stringify({
    connection: name,
    daemonPort: 9280,
    chromePid: null,
  }, null, 2), 'utf8');
  console.log(`[chrome-launch] Config written: ${configPath}`);

  // Spawn detached so Chrome survives daemon restart
  const child = spawn(chromePath, args, {
    detached: true,
    stdio: 'ignore',
  });

  child.unref();

  // Update config with actual Chrome PID
  writeFileSync(configPath, JSON.stringify({
    connection: name,
    daemonPort: 9280,
    chromePid: child.pid,
  }, null, 2), 'utf8');
  console.log(`[chrome-launch] Config updated with PID: ${child.pid}`);

  return {
    pid: child.pid,
    browserKind,
    profileDir,
    extensionDir,
  };
}

export async function killChromeForConnection(profileDir) {
  const { execSync } = await import('child_process');
  const execOpts = { encoding: 'utf-8', stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true };

  if (process.platform === 'win32') {
    // Try stored PID first (from cc-browser.json written at launch)
    let storedPid = null;
    const configPath = join(profileDir, 'cc-browser.json');
    if (existsSync(configPath)) {
      try {
        const config = JSON.parse(readFileSync(configPath, 'utf8'));
        if (config.chromePid) storedPid = config.chromePid;
      } catch {
        // Corrupted config, fall through to scan
      }
    }

    if (storedPid) {
      try {
        // Graceful kill of parent process (WM_CLOSE saves cookies/session)
        execSync(`taskkill /PID ${storedPid}`, execOpts);

        // Wait up to 5 seconds for graceful exit
        const deadline = Date.now() + 5000;
        while (Date.now() < deadline) {
          try {
            const count = execSync(
              `powershell -NoProfile -Command "Get-Process -Id ${storedPid} -ErrorAction SilentlyContinue | Measure-Object | Select-Object -ExpandProperty Count"`,
              execOpts,
            ).trim();
            if (count === '0') break;
          } catch {
            break;
          }
          await new Promise(r => setTimeout(r, 500));
        }

        // Force-kill with /T (tree kill gets children too) if still alive
        try {
          execSync(`taskkill /F /PID ${storedPid} /T`, execOpts);
        } catch {
          // Already gone
        }

        return { stopped: true };
      } catch {
        // Stored PID process not found, fall through to scan
      }
    }

    // Fallback: scan for processes matching this profile dir.
    // Use a SINGLE PowerShell invocation to find and kill all matching PIDs.
    try {
      const escapedDir = profileDir.replace(/'/g, "''");
      const ps = [
        `$pids = Get-CimInstance Win32_Process |`,
        `  Where-Object { $_.Name -match '(brave|chrome|msedge)\\.exe' -and $_.CommandLine -match [regex]::Escape('${escapedDir}') } |`,
        `  Select-Object -ExpandProperty ProcessId;`,
        `if ($pids) { $pids | ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue } }`,
      ].join(' ');
      execSync(`powershell -NoProfile -Command "${ps}"`, execOpts);
      return { stopped: true };
    } catch {
      return { stopped: false };
    }
  }

  // Unix: find by user-data-dir argument
  try {
    execSync(`pkill -f "user-data-dir=${profileDir}"`, { stdio: 'pipe', windowsHide: true });
    return { stopped: true };
  } catch {
    return { stopped: false };
  }
}
