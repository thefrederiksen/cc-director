// CC Fox Browser - Camoufox Browser Launch & Management
// Launches Camoufox (anti-detection Firefox) with persistent profiles per workspace.
// Tab tracking uses incrementing IDs via Map (no CDP needed).

import { existsSync, mkdirSync, rmSync, readdirSync } from 'fs';
import { join } from 'path';
import { homedir } from 'os';

// ---------------------------------------------------------------------------
// CJS workaround: camoufox npm package has an ESM bundling bug.
// Use require() to load from the CJS dist.
// ---------------------------------------------------------------------------

import { createRequire } from 'module';
const require = createRequire(import.meta.url);

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

function getBaseDir() {
  const localAppData = process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local');
  return join(localAppData, 'cc-fox-browser');
}

function getProfileDir(workspace) {
  return join(getBaseDir(), `camoufox-${workspace}`);
}

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

/** @type {import('playwright-core').Browser | null} */
let activeBrowser = null;

/** @type {import('playwright-core').BrowserContext | null} */
let activeContext = null;

/** @type {string | null} */
let activeWorkspace = null;

/** @type {number} */
let nextTabId = 1;

/** @type {Map<string, import('playwright-core').Page>} Tab ID -> Page */
const tabMap = new Map();

/** @type {Map<import('playwright-core').Page, string>} Page -> Tab ID (reverse lookup) */
const pageToTab = new Map();

/** @type {string | null} Most recently focused tab */
let activeTabId = null;

// ---------------------------------------------------------------------------
// Tab Tracking
// ---------------------------------------------------------------------------

function assignTabId(page) {
  const tabId = `t${nextTabId++}`;
  tabMap.set(tabId, page);
  pageToTab.set(page, tabId);

  page.on('close', () => {
    tabMap.delete(tabId);
    pageToTab.delete(page);
    if (activeTabId === tabId) {
      // Pick next available tab
      const remaining = Array.from(tabMap.keys());
      activeTabId = remaining.length > 0 ? remaining[remaining.length - 1] : null;
    }
  });

  activeTabId = tabId;
  return tabId;
}

function trackExistingPages(context) {
  for (const page of context.pages()) {
    if (!pageToTab.has(page)) {
      assignTabId(page);
    }
  }
}

// ---------------------------------------------------------------------------
// Camoufox Launch
// ---------------------------------------------------------------------------

/**
 * Launch Camoufox with a persistent profile for the given workspace.
 * @param {Object} opts
 * @param {string} [opts.workspace='default'] - Workspace name
 * @param {boolean} [opts.headless=false] - Run headless
 * @returns {Promise<Object>} Launch result
 */
export async function launchCamoufox(opts = {}) {
  const workspace = opts.workspace || 'default';
  const headless = opts.headless || false;

  // If already running for this workspace, return existing
  if (activeContext && activeWorkspace === workspace) {
    const connected = activeBrowser ? activeBrowser.isConnected() : (tabMap.size > 0);
    if (connected) {
      const tabs = listTabs();
      return {
        started: false,
        workspace,
        tabs,
        activeTab: activeTabId,
      };
    }
    // Browser disconnected, clean up
    await cleanup();
  }

  // If running for a different workspace, stop first
  if (activeContext) {
    await stopBrowser();
  }

  const profileDir = getProfileDir(workspace);
  if (!existsSync(profileDir)) {
    mkdirSync(profileDir, { recursive: true });
  }

  // Clean up stale lock files that prevent launch
  const lockFile = join(profileDir, 'lock');
  const parentLock = join(profileDir, '.parentlock');
  for (const lf of [lockFile, parentLock]) {
    if (existsSync(lf)) {
      try {
        rmSync(lf, { force: true });
        console.log(`[cc-fox-browser] Removed stale lock: ${lf}`);
      } catch {
        // Ignore
      }
    }
  }

  console.log(`[cc-fox-browser] Launching Camoufox (workspace: ${workspace})...`);
  console.log(`[cc-fox-browser] Profile: ${profileDir}`);

  // Load Camoufox via CJS workaround
  const { Camoufox } = require('../node_modules/camoufox/dist/index.cjs');

  // When data_dir is set, Camoufox returns a BrowserContext (not Browser).
  // Without data_dir, it returns a Browser.
  const result = await Camoufox({
    headless,
    data_dir: profileDir,
  });

  // Detect whether we got a Browser or BrowserContext.
  // With data_dir, Camoufox returns a BrowserContext directly.
  // Without data_dir, it returns a Browser.
  if (typeof result.contexts === 'function') {
    // Got a Browser object
    activeBrowser = result;
    activeContext = result.contexts()[0] || null;
    if (!activeContext) {
      throw new Error('Camoufox launched but no browser context available');
    }
  } else {
    // Got a BrowserContext (persistent context mode)
    activeContext = result;
    // BrowserContext may or may not expose .browser()
    try {
      activeBrowser = result.browser();
    } catch {
      activeBrowser = null;
    }
  }

  activeWorkspace = workspace;

  // Track existing pages
  trackExistingPages(activeContext);

  // Listen for new pages
  activeContext.on('page', (page) => {
    const tabId = assignTabId(page);
    console.log(`[cc-fox-browser] New tab: ${tabId} (${page.url()})`);
  });

  // Handle browser disconnect
  if (activeBrowser) {
    activeBrowser.on('disconnected', () => {
      console.log('[cc-fox-browser] Browser disconnected');
      cleanup();
    });
  }
  // Also listen on context close
  activeContext.on('close', () => {
    console.log('[cc-fox-browser] Context closed');
    cleanup();
  });

  // If no pages exist, create one
  if (tabMap.size === 0) {
    const page = await activeContext.newPage();
    assignTabId(page);
  }

  const tabs = listTabs();
  console.log(`[cc-fox-browser] Ready (${tabs.length} tab(s))`);

  return {
    started: true,
    workspace,
    tabs,
    activeTab: activeTabId,
  };
}

// ---------------------------------------------------------------------------
// Stop & Cleanup
// ---------------------------------------------------------------------------

function cleanup() {
  tabMap.clear();
  pageToTab.clear();
  activeTabId = null;
  activeBrowser = null;
  activeContext = null;
  activeWorkspace = null;
  nextTabId = 1;
}

export async function stopBrowser() {
  if (!activeContext && !activeBrowser) {
    return { stopped: false, message: 'No browser running' };
  }

  const workspace = activeWorkspace;
  console.log(`[cc-fox-browser] Stopping browser (workspace: ${workspace})...`);

  try {
    if (activeBrowser) {
      await activeBrowser.close();
    } else if (activeContext) {
      await activeContext.close();
    }
  } catch {
    // Already closed
  }

  cleanup();
  return { stopped: true, workspace };
}

// ---------------------------------------------------------------------------
// Tab Operations
// ---------------------------------------------------------------------------

export function listTabs() {
  const tabs = [];
  for (const [tabId, page] of tabMap) {
    tabs.push({
      tabId,
      url: page.url(),
      title: '', // Title requires async, filled by caller if needed
    });
  }
  return tabs;
}

export async function listTabsAsync() {
  const tabs = [];
  for (const [tabId, page] of tabMap) {
    tabs.push({
      tabId,
      url: page.url(),
      title: await page.title().catch(() => ''),
    });
  }
  return tabs;
}

export async function openTab(url) {
  if (!activeContext) {
    throw new Error('No browser running. Run "start" first.');
  }

  const page = await activeContext.newPage();
  const tabId = assignTabId(page);

  const targetUrl = (url || '').trim() || 'about:blank';
  if (targetUrl !== 'about:blank') {
    await page.goto(targetUrl, { timeout: 30000 }).catch(() => {});
  }

  return {
    tabId,
    url: page.url(),
    title: await page.title().catch(() => ''),
  };
}

export async function closeTab(tabId) {
  const page = tabMap.get(tabId);
  if (!page) {
    throw new Error(`Tab not found: ${tabId}`);
  }
  await page.close();
  return { closed: tabId };
}

export async function focusTab(tabId) {
  const page = tabMap.get(tabId);
  if (!page) {
    throw new Error(`Tab not found: ${tabId}`);
  }
  await page.bringToFront();
  activeTabId = tabId;
  return { focused: tabId };
}

// ---------------------------------------------------------------------------
// Page Resolution
// ---------------------------------------------------------------------------

/**
 * Get the Page for a given tab ID (or active tab).
 * @param {string} [tabId] - Tab ID, or null for active tab
 * @returns {import('playwright-core').Page}
 */
export function getPage(tabId) {
  if ((!activeBrowser && !activeContext) || tabMap.size === 0) {
    throw new Error('No browser running. Run "start" first.');
  }

  const resolvedId = tabId || activeTabId;
  if (!resolvedId) {
    // Return first available page
    const first = tabMap.values().next();
    if (first.done) throw new Error('No tabs available');
    return first.value;
  }

  const page = tabMap.get(resolvedId);
  if (!page) {
    throw new Error(`Tab not found: ${resolvedId}. Run "tabs" to see available tabs.`);
  }
  return page;
}

/**
 * Get the tab ID for a given Page.
 * @param {import('playwright-core').Page} page
 * @returns {string | null}
 */
export function getTabId(page) {
  return pageToTab.get(page) || null;
}

/**
 * Get the active tab ID.
 * @returns {string | null}
 */
export function getActiveTabId() {
  return activeTabId;
}

/**
 * Set the active tab ID.
 * @param {string} tabId
 */
export function setActiveTabId(tabId) {
  activeTabId = tabId;
}

// ---------------------------------------------------------------------------
// Status
// ---------------------------------------------------------------------------

export function isRunning() {
  if (activeBrowser) return activeBrowser.isConnected();
  // In persistent context mode (no browser object), check if context has pages
  return !!activeContext && tabMap.size > 0;
}

export function getWorkspace() {
  return activeWorkspace;
}

export function getBrowser() {
  return activeBrowser;
}

export function getContext() {
  return activeContext;
}
