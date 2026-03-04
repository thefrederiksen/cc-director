// CC Fox Browser - Browser Interactions
// All browser actions: click, type, press, navigate, etc.
// Adapted from cc-browser (uses tab-based resolution, no captcha detection).

import { getPage, getTabId } from './browser.mjs';
import { ensurePageState, refLocator, restoreRoleRefsForTab, getCurrentMode } from './session.mjs';
import {
  sleep,
  navigationDelay,
  preClickDelay,
  preTypeDelay,
  preScrollDelay,
  postLoadDelay,
  humanMousePath,
  clickOffset,
  typingDelays,
  randomInt,
} from './human-mode.mjs';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function normalizeTimeoutMs(value, defaultMs = 8000) {
  const ms = typeof value === 'number' && Number.isFinite(value) ? value : defaultMs;
  return Math.max(500, Math.min(60000, Math.floor(ms)));
}

function requireRef(ref) {
  const trimmed = String(ref || '').trim();
  if (!trimmed) {
    throw new Error('ref is required');
  }
  return trimmed;
}

/**
 * Resolve a locator from ref, text, or selector.
 */
export function resolveLocator(page, opts) {
  const { ref, text, selector, exact } = opts;
  const hasRef = ref && String(ref).trim();
  const hasText = text && String(text).trim();
  const hasSelector = selector && String(selector).trim();

  const count = (hasRef ? 1 : 0) + (hasText ? 1 : 0) + (hasSelector ? 1 : 0);
  if (count === 0) {
    throw new Error('One of --ref, --text, or --selector is required');
  }
  if (count > 1) {
    throw new Error('Only one of --ref, --text, or --selector can be used at a time');
  }

  if (hasRef) {
    const refStr = String(ref).trim();
    return { locator: refLocator(page, refStr), description: refStr };
  }

  if (hasText) {
    const textStr = String(text).trim();
    const locator = exact
      ? page.getByText(textStr, { exact: true }).first()
      : page.getByText(textStr).first();
    return { locator, description: `text="${textStr}"` };
  }

  const selectorStr = String(selector).trim();
  return { locator: page.locator(selectorStr).first(), description: `selector="${selectorStr}"` };
}

function toAIFriendlyError(err, ref) {
  const msg = err?.message || String(err);

  if (msg.includes('Timeout') || msg.includes('timeout')) {
    return new Error(
      `Element "${ref}" not found or not visible within timeout. ` +
        `Try scrolling the element into view or waiting for it to appear.`
    );
  }

  if (msg.includes('resolved to') && msg.includes('elements')) {
    return new Error(
      `Multiple elements matched "${ref}". Run a new snapshot to get updated refs.`
    );
  }

  if (msg.includes('not attached') || msg.includes('detached')) {
    return new Error(
      `Element "${ref}" is no longer attached to the DOM. Run a new snapshot.`
    );
  }

  return new Error(`Action failed on "${ref}": ${msg}`);
}

// ---------------------------------------------------------------------------
// Navigation
// ---------------------------------------------------------------------------

export async function navigate(opts) {
  const { tabId, url, waitUntil = 'load', timeoutMs } = opts;
  const page = getPage(tabId);
  ensurePageState(page);

  const mode = getCurrentMode();
  if (mode !== 'fast') {
    await sleep(navigationDelay());
  }

  const timeout = normalizeTimeoutMs(timeoutMs, 30000);
  await page.goto(url, { waitUntil, timeout });

  if (mode !== 'fast') {
    await sleep(postLoadDelay());
  }

  return {
    url: page.url(),
    title: await page.title().catch(() => ''),
  };
}

export async function reload(opts) {
  const { tabId, waitUntil = 'load', timeoutMs } = opts;
  const page = getPage(tabId);
  ensurePageState(page);

  const timeout = normalizeTimeoutMs(timeoutMs, 30000);
  await page.reload({ waitUntil, timeout });

  return {
    url: page.url(),
    title: await page.title().catch(() => ''),
  };
}

export async function goBack(opts) {
  const { tabId, waitUntil = 'commit', timeoutMs } = opts;
  const page = getPage(tabId);
  ensurePageState(page);

  const timeout = normalizeTimeoutMs(timeoutMs, 30000);
  await page.goBack({ waitUntil, timeout });

  return {
    url: page.url(),
    title: await page.title().catch(() => ''),
  };
}

export async function goForward(opts) {
  const { tabId, waitUntil = 'commit', timeoutMs } = opts;
  const page = getPage(tabId);
  ensurePageState(page);

  const timeout = normalizeTimeoutMs(timeoutMs, 30000);
  await page.goForward({ waitUntil, timeout });

  return {
    url: page.url(),
    title: await page.title().catch(() => ''),
  };
}

// ---------------------------------------------------------------------------
// Element Interactions
// ---------------------------------------------------------------------------

export async function click(opts) {
  const { tabId, ref, text, selector, exact, doubleClick, button, modifiers, timeoutMs } = opts;
  const page = getPage(tabId);
  ensurePageState(page);
  restoreRoleRefsForTab({ tabId: tabId || getTabId(page), page });

  const { locator, description } = resolveLocator(page, { ref, text, selector, exact });
  const timeout = normalizeTimeoutMs(timeoutMs);

  const mode = getCurrentMode();
  if (mode !== 'fast') {
    await sleep(preClickDelay());
    try {
      const box = await locator.boundingBox();
      if (box) {
        const centerX = box.x + box.width / 2;
        const centerY = box.y + box.height / 2;
        const offset = clickOffset();
        const targetX = centerX + offset.x;
        const targetY = centerY + offset.y;
        const path = humanMousePath(0, 0, targetX, targetY);
        for (const point of path) {
          await page.mouse.move(point.x, point.y);
        }
      }
    } catch {
      // Bounding box may fail for offscreen elements
    }
  }

  try {
    if (doubleClick) {
      await locator.dblclick({ timeout, button, modifiers });
    } else {
      await locator.click({ timeout, button, modifiers });
    }
  } catch (err) {
    throw toAIFriendlyError(err, description);
  }
}

// ---------------------------------------------------------------------------
// Text Input
// ---------------------------------------------------------------------------

export async function pressKey(opts) {
  const { tabId, key, delayMs } = opts;
  const page = getPage(tabId);
  ensurePageState(page);

  const keyStr = String(key || '').trim();
  if (!keyStr) {
    throw new Error('key is required');
  }

  const mode = getCurrentMode();
  if (mode !== 'fast') {
    await sleep(preClickDelay());
  }

  await page.keyboard.press(keyStr, {
    delay: Math.max(0, Math.floor(delayMs || 0)),
  });
}

export async function type(opts) {
  const { tabId, ref, text, textContent, selector, exact, submit, slowly, timeoutMs } = opts;
  const page = getPage(tabId);
  ensurePageState(page);
  restoreRoleRefsForTab({ tabId: tabId || getTabId(page), page });

  const { locator, description } = resolveLocator(page, {
    ref,
    text: textContent,
    selector,
    exact,
  });
  const textStr = String(text ?? '');
  const timeout = normalizeTimeoutMs(timeoutMs);

  const mode = getCurrentMode();

  try {
    if (mode !== 'fast') {
      await sleep(preTypeDelay());
      await locator.click({ timeout });
      const delays = typingDelays(textStr);
      for (let i = 0; i < textStr.length; i++) {
        await page.keyboard.type(textStr[i], { delay: 0 });
        if (i < textStr.length - 1) {
          await sleep(delays[i]);
        }
      }
    } else if (slowly) {
      await locator.click({ timeout });
      await locator.type(textStr, { timeout, delay: 75 });
    } else {
      await locator.fill(textStr, { timeout });
    }
    if (submit) {
      if (mode !== 'fast') await sleep(preClickDelay());
      await locator.press('Enter', { timeout });
    }
  } catch (err) {
    throw toAIFriendlyError(err, description);
  }
}

// ---------------------------------------------------------------------------
// JavaScript Evaluation
// ---------------------------------------------------------------------------

export async function evaluate(opts) {
  const { tabId, fn, ref } = opts;
  const page = getPage(tabId);
  ensurePageState(page);
  restoreRoleRefsForTab({ tabId: tabId || getTabId(page), page });

  const fnText = String(fn || '').trim();
  if (!fnText) {
    throw new Error('function is required');
  }

  if (ref) {
    const locator = refLocator(page, ref);
    const elementEvaluator = new Function(
      'el',
      'fnBody',
      `
      "use strict";
      try {
        var candidate = eval("(" + fnBody + ")");
        return typeof candidate === "function" ? candidate(el) : candidate;
      } catch (err) {
        throw new Error("Invalid evaluate function: " + (err && err.message ? err.message : String(err)));
      }
      `
    );
    return await locator.evaluate(elementEvaluator, fnText);
  }

  const browserEvaluator = new Function(
    'fnBody',
    `
    "use strict";
    try {
      var candidate = eval("(" + fnBody + ")");
      return typeof candidate === "function" ? candidate() : candidate;
    } catch (err) {
      throw new Error("Invalid evaluate function: " + (err && err.message ? err.message : String(err)));
    }
    `
  );
  return await page.evaluate(browserEvaluator, fnText);
}

// ---------------------------------------------------------------------------
// Wait
// ---------------------------------------------------------------------------

export async function waitFor(opts) {
  const { tabId, timeMs, text, textGone, selector, url, loadState, fn, timeoutMs } = opts;
  const page = getPage(tabId);
  ensurePageState(page);
  const timeout = normalizeTimeoutMs(timeoutMs, 20000);

  if (typeof timeMs === 'number' && Number.isFinite(timeMs)) {
    await page.waitForTimeout(Math.max(0, timeMs));
  }

  if (text) {
    await page.getByText(text).first().waitFor({ state: 'visible', timeout });
  }

  if (textGone) {
    await page.getByText(textGone).first().waitFor({ state: 'hidden', timeout });
  }

  if (selector) {
    const sel = String(selector).trim();
    if (sel) {
      await page.locator(sel).first().waitFor({ state: 'visible', timeout });
    }
  }

  if (url) {
    const urlStr = String(url).trim();
    if (urlStr) {
      await page.waitForURL(urlStr, { timeout });
    }
  }

  if (loadState) {
    await page.waitForLoadState(loadState, { timeout });
  }

  if (fn) {
    const fnStr = String(fn).trim();
    if (fnStr) {
      await page.waitForFunction(fnStr, { timeout });
    }
  }
}

// ---------------------------------------------------------------------------
// Screenshots
// ---------------------------------------------------------------------------

export async function takeScreenshot(opts) {
  const { tabId, ref, element, fullPage, type = 'png' } = opts;
  const page = getPage(tabId);
  ensurePageState(page);
  restoreRoleRefsForTab({ tabId: tabId || getTabId(page), page });

  if (ref) {
    if (fullPage) {
      throw new Error('fullPage is not supported for element screenshots');
    }
    const locator = refLocator(page, ref);
    const buffer = await locator.screenshot({ type });
    return { buffer };
  }

  if (element) {
    if (fullPage) {
      throw new Error('fullPage is not supported for element screenshots');
    }
    const locator = page.locator(element).first();
    const buffer = await locator.screenshot({ type });
    return { buffer };
  }

  const buffer = await page.screenshot({ type, fullPage: Boolean(fullPage) });
  return { buffer };
}
