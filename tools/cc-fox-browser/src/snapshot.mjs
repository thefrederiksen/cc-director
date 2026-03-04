// CC Fox Browser - Page Snapshots
// Generates role-based element refs from accessibility tree.
// Adapted from cc-browser (uses tab-based resolution instead of CDP).

import { getPage, getTabId } from './browser.mjs';
import { ensurePageState, storeRoleRefsForTab, restoreRoleRefsForTab, refLocator } from './session.mjs';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const INTERACTIVE_ROLES = new Set([
  'button',
  'link',
  'textbox',
  'checkbox',
  'radio',
  'combobox',
  'listbox',
  'menuitem',
  'menuitemcheckbox',
  'menuitemradio',
  'option',
  'searchbox',
  'slider',
  'spinbutton',
  'switch',
  'tab',
  'treeitem',
]);

const CONTENT_ROLES = new Set([
  'heading',
  'cell',
  'gridcell',
  'columnheader',
  'rowheader',
  'listitem',
  'article',
  'region',
  'main',
  'navigation',
]);

const STRUCTURAL_ROLES = new Set([
  'generic',
  'group',
  'list',
  'table',
  'row',
  'rowgroup',
  'grid',
  'treegrid',
  'menu',
  'menubar',
  'toolbar',
  'tablist',
  'tree',
  'directory',
  'document',
  'application',
  'presentation',
  'none',
]);

// ---------------------------------------------------------------------------
// Role Name Tracking
// ---------------------------------------------------------------------------

function createRoleNameTracker() {
  const counts = new Map();
  const refsByKey = new Map();

  return {
    counts,
    refsByKey,

    getKey(role, name) {
      return `${role}:${name ?? ''}`;
    },

    getNextIndex(role, name) {
      const key = this.getKey(role, name);
      const current = counts.get(key) ?? 0;
      counts.set(key, current + 1);
      return current;
    },

    trackRef(role, name, ref) {
      const key = this.getKey(role, name);
      const list = refsByKey.get(key) ?? [];
      list.push(ref);
      refsByKey.set(key, list);
    },

    getDuplicateKeys() {
      const out = new Set();
      for (const [key, refs] of refsByKey) {
        if (refs.length > 1) {
          out.add(key);
        }
      }
      return out;
    },
  };
}

function removeNthFromNonDuplicates(refs, tracker) {
  const duplicates = tracker.getDuplicateKeys();
  for (const [ref, data] of Object.entries(refs)) {
    const key = tracker.getKey(data.role, data.name);
    if (!duplicates.has(key)) {
      delete refs[ref]?.nth;
    }
  }
}

// ---------------------------------------------------------------------------
// Snapshot Parsing
// ---------------------------------------------------------------------------

function getIndentLevel(line) {
  const match = line.match(/^(\s*)/);
  return match ? Math.floor(match[1].length / 2) : 0;
}

function compactTree(tree) {
  const lines = tree.split('\n');
  const result = [];

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (line.includes('[ref=')) {
      result.push(line);
      continue;
    }
    if (line.includes(':') && !line.trimEnd().endsWith(':')) {
      result.push(line);
      continue;
    }

    const currentIndent = getIndentLevel(line);
    let hasRelevantChildren = false;
    for (let j = i + 1; j < lines.length; j++) {
      const childIndent = getIndentLevel(lines[j]);
      if (childIndent <= currentIndent) break;
      if (lines[j]?.includes('[ref=')) {
        hasRelevantChildren = true;
        break;
      }
    }
    if (hasRelevantChildren) {
      result.push(line);
    }
  }

  return result.join('\n');
}

/**
 * Build role snapshot from Playwright's ariaSnapshot output
 */
export function buildRoleSnapshotFromAriaSnapshot(ariaSnapshot, options = {}) {
  const lines = ariaSnapshot.split('\n');
  const refs = {};
  const tracker = createRoleNameTracker();

  let counter = 0;
  const nextRef = () => {
    counter += 1;
    return `e${counter}`;
  };

  if (options.interactive) {
    const result = [];
    for (const line of lines) {
      const depth = getIndentLevel(line);
      if (options.maxDepth !== undefined && depth > options.maxDepth) {
        continue;
      }

      const match = line.match(/^(\s*-\s*)(\w+)(?:\s+"([^"]*)")?(.*)$/);
      if (!match) continue;
      const [, , roleRaw, name, suffix] = match;
      if (roleRaw.startsWith('/')) continue;

      const role = roleRaw.toLowerCase();
      if (!INTERACTIVE_ROLES.has(role)) continue;

      const ref = nextRef();
      const nth = tracker.getNextIndex(role, name);
      tracker.trackRef(role, name, ref);
      refs[ref] = { role, name, nth };

      let enhanced = `- ${roleRaw}`;
      if (name) enhanced += ` "${name}"`;
      enhanced += ` [ref=${ref}]`;
      if (nth > 0) enhanced += ` [nth=${nth}]`;
      if (suffix.includes('[')) enhanced += suffix;
      result.push(enhanced);
    }

    removeNthFromNonDuplicates(refs, tracker);

    return {
      snapshot: result.join('\n') || '(no interactive elements)',
      refs,
    };
  }

  // Full snapshot
  const result = [];
  for (const line of lines) {
    const depth = getIndentLevel(line);
    if (options.maxDepth !== undefined && depth > options.maxDepth) {
      continue;
    }

    const match = line.match(/^(\s*-\s*)(\w+)(?:\s+"([^"]*)")?(.*)$/);
    if (!match) {
      if (!options.interactive) result.push(line);
      continue;
    }

    const [, prefix, roleRaw, name, suffix] = match;
    if (roleRaw.startsWith('/')) {
      if (!options.interactive) result.push(line);
      continue;
    }

    const role = roleRaw.toLowerCase();
    const isInteractive = INTERACTIVE_ROLES.has(role);
    const isContent = CONTENT_ROLES.has(role);
    const isStructural = STRUCTURAL_ROLES.has(role);

    if (options.compact && isStructural && !name) {
      continue;
    }

    const shouldHaveRef = isInteractive || (isContent && name);
    if (!shouldHaveRef) {
      result.push(line);
      continue;
    }

    const ref = nextRef();
    const nth = tracker.getNextIndex(role, name);
    tracker.trackRef(role, name, ref);
    refs[ref] = { role, name, nth };

    let enhanced = `${prefix}${roleRaw}`;
    if (name) enhanced += ` "${name}"`;
    enhanced += ` [ref=${ref}]`;
    if (nth > 0) enhanced += ` [nth=${nth}]`;
    if (suffix) enhanced += suffix;
    result.push(enhanced);
  }

  removeNthFromNonDuplicates(refs, tracker);

  const tree = result.join('\n') || '(empty)';
  return {
    snapshot: options.compact ? compactTree(tree) : tree,
    refs,
  };
}

// ---------------------------------------------------------------------------
// Snapshot Functions
// ---------------------------------------------------------------------------

export async function snapshot(opts = {}) {
  const { tabId, interactive, compact, maxDepth, maxChars = 10000 } = opts;
  const page = getPage(tabId);
  ensurePageState(page);

  // Get Playwright's ariaSnapshot
  const ariaSnapshot = await page.locator('body').ariaSnapshot({ timeout: 10000 });

  // Parse into role refs
  const { snapshot: snapshotText, refs } = buildRoleSnapshotFromAriaSnapshot(ariaSnapshot, {
    interactive,
    compact,
    maxDepth,
  });

  // Store refs for later use
  const resolvedTabId = tabId || getTabId(page);
  storeRoleRefsForTab({
    page,
    tabId: resolvedTabId,
    refs,
    mode: 'role',
  });

  // Truncate if needed
  let finalSnapshot = snapshotText;
  if (maxChars && snapshotText.length > maxChars) {
    finalSnapshot = snapshotText.slice(0, maxChars) + '\n... (truncated)';
  }

  return {
    snapshot: finalSnapshot,
    refs,
    stats: {
      chars: snapshotText.length,
      lines: snapshotText.split('\n').length,
      refs: Object.keys(refs).length,
      interactive: Object.values(refs).filter((r) => INTERACTIVE_ROLES.has(r.role)).length,
    },
  };
}

/**
 * Get page info: URL, title, viewport
 */
export async function getPageInfo(opts = {}) {
  const { tabId } = opts;
  const page = getPage(tabId);
  ensurePageState(page);

  const viewport = page.viewportSize();

  return {
    url: page.url(),
    title: await page.title().catch(() => ''),
    viewport: viewport || { width: 0, height: 0 },
  };
}

/**
 * Get page text content
 */
export async function getTextContent(opts = {}) {
  const { tabId, selector } = opts;
  const page = getPage(tabId);
  ensurePageState(page);

  if (selector) {
    const locator = page.locator(selector).first();
    return await locator.textContent();
  }

  return await page.locator('body').textContent();
}
