// CC Fox Browser - Session Management
// Manages page state and element refs.
// Simplified from cc-browser: no CDP, uses tab IDs from browser.mjs.

// ---------------------------------------------------------------------------
// Mode State
// ---------------------------------------------------------------------------

let currentMode = 'human'; // 'fast' | 'human'

export function getCurrentMode() {
  return currentMode;
}

export function setCurrentMode(mode) {
  if (!['fast', 'human'].includes(mode)) {
    throw new Error(`Invalid mode: ${mode}. Must be fast or human`);
  }
  currentMode = mode;
}

// ---------------------------------------------------------------------------
// Page State
// ---------------------------------------------------------------------------

/**
 * @typedef {Object} RoleRef
 * @property {string} role
 * @property {string} [name]
 * @property {number} [nth]
 */

/**
 * @typedef {Object} PageState
 * @property {Array} console
 * @property {Array} errors
 * @property {Array} requests
 * @property {WeakMap<object, string>} requestIds
 * @property {number} nextRequestId
 * @property {Record<string, RoleRef>} [roleRefs]
 * @property {string} [roleRefsFrameSelector]
 * @property {string} [roleRefsMode]
 */

const MAX_CONSOLE_MESSAGES = 500;
const MAX_PAGE_ERRORS = 200;
const MAX_NETWORK_REQUESTS = 500;

/** @type {WeakMap<object, PageState>} */
const pageStates = new WeakMap();

/** @type {WeakSet<object>} */
const observedPages = new WeakSet();

/** @type {Map<string, {refs: Record<string, RoleRef>, frameSelector?: string, mode?: string}>} */
const roleRefsByTab = new Map();
const MAX_ROLE_REFS_CACHE = 50;

// ---------------------------------------------------------------------------
// Role Refs Management
// ---------------------------------------------------------------------------

export function storeRoleRefsForTab(opts) {
  const { page, tabId, refs, frameSelector, mode } = opts;
  const state = ensurePageState(page);
  state.roleRefs = refs;
  state.roleRefsFrameSelector = frameSelector;
  state.roleRefsMode = mode;

  if (tabId) {
    roleRefsByTab.set(tabId, {
      refs,
      ...(frameSelector ? { frameSelector } : {}),
      ...(mode ? { mode } : {}),
    });

    // LRU-like cleanup
    while (roleRefsByTab.size > MAX_ROLE_REFS_CACHE) {
      const first = roleRefsByTab.keys().next();
      if (first.done) break;
      roleRefsByTab.delete(first.value);
    }
  }
}

export function restoreRoleRefsForTab(opts) {
  const { tabId, page } = opts;
  if (!tabId) return;

  const cached = roleRefsByTab.get(tabId);
  if (!cached) return;

  const state = ensurePageState(page);
  if (state.roleRefs) return; // Already have refs

  state.roleRefs = cached.refs;
  state.roleRefsFrameSelector = cached.frameSelector;
  state.roleRefsMode = cached.mode;
}

// ---------------------------------------------------------------------------
// Page State Management
// ---------------------------------------------------------------------------

export function ensurePageState(page) {
  const existing = pageStates.get(page);
  if (existing) return existing;

  /** @type {PageState} */
  const state = {
    console: [],
    errors: [],
    requests: [],
    requestIds: new WeakMap(),
    nextRequestId: 0,
  };
  pageStates.set(page, state);

  if (!observedPages.has(page)) {
    observedPages.add(page);

    page.on('console', (msg) => {
      state.console.push({
        type: msg.type(),
        text: msg.text(),
        timestamp: new Date().toISOString(),
        location: msg.location(),
      });
      if (state.console.length > MAX_CONSOLE_MESSAGES) {
        state.console.shift();
      }
    });

    page.on('pageerror', (err) => {
      state.errors.push({
        message: err?.message ? String(err.message) : String(err),
        name: err?.name ? String(err.name) : undefined,
        stack: err?.stack ? String(err.stack) : undefined,
        timestamp: new Date().toISOString(),
      });
      if (state.errors.length > MAX_PAGE_ERRORS) {
        state.errors.shift();
      }
    });

    page.on('request', (req) => {
      state.nextRequestId += 1;
      const id = `r${state.nextRequestId}`;
      state.requestIds.set(req, id);
      state.requests.push({
        id,
        timestamp: new Date().toISOString(),
        method: req.method(),
        url: req.url(),
        resourceType: req.resourceType(),
      });
      if (state.requests.length > MAX_NETWORK_REQUESTS) {
        state.requests.shift();
      }
    });

    page.on('response', (resp) => {
      const req = resp.request();
      const id = state.requestIds.get(req);
      if (!id) return;
      const rec = state.requests.findLast((r) => r.id === id);
      if (rec) {
        rec.status = resp.status();
        rec.ok = resp.ok();
      }
    });

    page.on('requestfailed', (req) => {
      const id = state.requestIds.get(req);
      if (!id) return;
      const rec = state.requests.findLast((r) => r.id === id);
      if (rec) {
        rec.failureText = req.failure()?.errorText;
        rec.ok = false;
      }
    });

    page.on('close', () => {
      pageStates.delete(page);
      observedPages.delete(page);
    });
  }

  return state;
}

export function getPageState(page) {
  return pageStates.get(page);
}

// ---------------------------------------------------------------------------
// Ref Locator
// ---------------------------------------------------------------------------

export function refLocator(page, ref) {
  const normalized = ref.startsWith('@')
    ? ref.slice(1)
    : ref.startsWith('ref=')
      ? ref.slice(4)
      : ref;

  if (/^e\d+$/i.test(normalized)) {
    const state = pageStates.get(page);

    const info = state?.roleRefs?.[normalized.toLowerCase()];
    if (!info) {
      throw new Error(
        `Unknown ref "${normalized}". Run a new snapshot to get current element refs.`
      );
    }

    const scope = state?.roleRefsFrameSelector
      ? page.frameLocator(state.roleRefsFrameSelector)
      : page;

    const locator = info.name
      ? scope.getByRole(info.role, { name: info.name, exact: true })
      : scope.getByRole(info.role);

    return info.nth !== undefined ? locator.nth(info.nth) : locator;
  }

  // Fallback: aria-ref
  return page.locator(`aria-ref=${normalized}`);
}
