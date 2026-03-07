// CC Browser v2 - Extension Background Service Worker
// Routes commands between native messaging host and content scripts.

const NATIVE_HOST_NAME = 'com.cc_browser.bridge';

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

let nativePort = null;
const pendingRequests = new Map(); // id -> { resolve, reject, timer }

// ---------------------------------------------------------------------------
// Native Messaging Connection
// ---------------------------------------------------------------------------

function connectNative() {
  if (nativePort) return;

  try {
    nativePort = chrome.runtime.connectNative(NATIVE_HOST_NAME);
    console.log('[cc-browser] Native host connected');

    nativePort.onMessage.addListener((msg) => {
      handleNativeMessage(msg);
    });

    nativePort.onDisconnect.addListener(() => {
      const err = chrome.runtime.lastError;
      console.log('[cc-browser] Native host disconnected:', err?.message || 'unknown');
      nativePort = null;

      // Reject all pending requests
      for (const [id, pending] of pendingRequests) {
        clearTimeout(pending.timer);
        pending.reject(new Error('Native host disconnected'));
      }
      pendingRequests.clear();

      // Reconnect after delay
      setTimeout(() => connectNative(), 2000);
    });
  } catch (err) {
    console.error('[cc-browser] Failed to connect native host:', err.message);
    setTimeout(() => connectNative(), 5000);
  }
}

// ---------------------------------------------------------------------------
// Message Handling
// ---------------------------------------------------------------------------

function handleNativeMessage(msg) {
  if (!msg || !msg.id) {
    console.warn('[cc-browser] Received message without id:', msg);
    return;
  }

  // This is a command from the daemon via native host
  if (msg.command) {
    handleCommand(msg);
    return;
  }

  // This is a response to a request we sent
  const pending = pendingRequests.get(msg.id);
  if (pending) {
    clearTimeout(pending.timer);
    pendingRequests.delete(msg.id);
    if (msg.error) {
      pending.reject(new Error(msg.error));
    } else {
      pending.resolve(msg.result);
    }
  }
}

async function handleCommand(msg) {
  const { id, command, params } = msg;
  let result;
  let error;

  try {
    switch (command) {
      case 'ping':
        result = cmdPing(params);
        break;
      case 'tabs':
        result = await cmdTabs(params);
        break;
      case 'tabs.open':
        result = await cmdTabOpen(params);
        break;
      case 'tabs.close':
        result = await cmdTabClose(params);
        break;
      case 'tabs.focus':
        result = await cmdTabFocus(params);
        break;
      case 'navigate':
        result = await cmdNavigate(params);
        break;
      case 'back':
        result = await cmdBack(params);
        break;
      case 'forward':
        result = await cmdForward(params);
        break;
      case 'reload':
        result = await cmdReload(params);
        break;
      case 'screenshot':
        result = await cmdScreenshot(params);
        break;
      case 'cookies.list':
        result = await cmdCookiesList(params);
        break;
      case 'cookies.set':
        result = await cmdCookiesSet(params);
        break;
      case 'cookies.delete':
        result = await cmdCookiesDelete(params);
        break;
      case 'cookies.export':
        result = await cmdCookiesExport(params);
        break;
      case 'links':
        result = await forwardToContent('links', params);
        break;
      case 'snapshot':
        result = await cmdSnapshot(params);
        break;
      case 'evaluate':
        result = await cmdEvaluate(params);
        break;
      case 'click':
      case 'dblclick':
      case 'hover':
      case 'type':
      case 'fill':
      case 'press':
      case 'select':
      case 'scroll':
      case 'drag':
      case 'wait':
      case 'getText':
      case 'getHtml':
      case 'getInfo':
      case 'upload':
        result = await forwardToContent(command, params);
        break;
      case 'paste':
        result = await cmdPaste(params);
        break;
      default:
        error = `Unknown command: ${command}`;
    }
  } catch (err) {
    error = err.message || String(err);
  }

  sendToNative({ id, result, error });
}

// ---------------------------------------------------------------------------
// Command Handlers
// ---------------------------------------------------------------------------

function cmdPing() {
  return { pong: true, timestamp: Date.now() };
}

async function cmdTabs() {
  const tabs = await chrome.tabs.query({});
  return tabs.map(t => ({
    tabId: t.id,
    url: t.url || '',
    title: t.title || '',
    active: t.active,
    windowId: t.windowId,
  }));
}

async function cmdTabOpen(params) {
  const tab = await chrome.tabs.create({
    url: params?.url || 'about:blank',
    active: params?.active !== false,
  });
  return { tabId: tab.id, url: tab.url || params?.url || 'about:blank' };
}

async function cmdTabClose(params) {
  const tabId = parseInt(params?.tabId, 10);
  if (!tabId) throw new Error('tabId is required');
  await chrome.tabs.remove(tabId);
  return { closed: tabId };
}

async function cmdTabFocus(params) {
  const tabId = parseInt(params?.tabId, 10);
  if (!tabId) throw new Error('tabId is required');
  await chrome.tabs.update(tabId, { active: true });
  const tab = await chrome.tabs.get(tabId);
  if (tab.windowId) {
    await chrome.windows.update(tab.windowId, { focused: true });
  }
  return { focused: tabId };
}

async function cmdNavigate(params) {
  const tabId = await resolveTabId(params);
  const url = params?.url;
  if (!url) throw new Error('url is required');

  await chrome.tabs.update(tabId, { url });

  // Wait for navigation to complete
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      chrome.tabs.onUpdated.removeListener(listener);
      reject(new Error('Navigation timeout (30s)'));
    }, params?.timeout || 30000);

    function listener(updatedTabId, changeInfo, tab) {
      if (updatedTabId !== tabId) return;
      if (changeInfo.status === 'complete') {
        clearTimeout(timeout);
        chrome.tabs.onUpdated.removeListener(listener);
        resolve({ url: tab.url, title: tab.title });
      }
    }

    chrome.tabs.onUpdated.addListener(listener);
  });
}

async function cmdBack(params) {
  const tabId = await resolveTabId(params);
  await chrome.tabs.goBack(tabId);
  // Wait for navigation to settle
  await new Promise(r => setTimeout(r, 500));
  const tab = await chrome.tabs.get(tabId);
  return { url: tab.url, title: tab.title };
}

async function cmdForward(params) {
  const tabId = await resolveTabId(params);
  await chrome.tabs.goForward(tabId);
  await new Promise(r => setTimeout(r, 500));
  const tab = await chrome.tabs.get(tabId);
  return { url: tab.url, title: tab.title };
}

async function cmdReload(params) {
  const tabId = await resolveTabId(params);
  await chrome.tabs.reload(tabId);
  // Wait for reload to complete
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      chrome.tabs.onUpdated.removeListener(listener);
      reject(new Error('Reload timeout (30s)'));
    }, params?.timeout || 30000);

    function listener(updatedTabId, changeInfo, tab) {
      if (updatedTabId !== tabId) return;
      if (changeInfo.status === 'complete') {
        clearTimeout(timeout);
        chrome.tabs.onUpdated.removeListener(listener);
        resolve({ url: tab.url, title: tab.title });
      }
    }

    chrome.tabs.onUpdated.addListener(listener);
  });
}

async function cmdCookiesList(params) {
  const details = {};
  if (params?.domain) details.domain = params.domain;
  if (params?.url) details.url = params.url;
  if (params?.name) details.name = params.name;
  const cookies = await chrome.cookies.getAll(details);
  return cookies.map(c => ({
    name: c.name,
    value: c.value,
    domain: c.domain,
    path: c.path,
    secure: c.secure,
    httpOnly: c.httpOnly,
    expirationDate: c.expirationDate,
  }));
}

async function cmdCookiesSet(params) {
  if (!params?.url) throw new Error('url is required');
  if (!params?.name) throw new Error('name is required');
  const cookie = await chrome.cookies.set({
    url: params.url,
    name: params.name,
    value: params.value || '',
    domain: params.domain,
    path: params.path || '/',
    secure: params.secure,
    httpOnly: params.httpOnly,
    expirationDate: params.expirationDate,
  });
  return { set: true, name: cookie.name, domain: cookie.domain };
}

async function cmdCookiesDelete(params) {
  if (!params?.url) throw new Error('url is required');
  if (!params?.name) throw new Error('name is required');
  await chrome.cookies.remove({ url: params.url, name: params.name });
  return { deleted: true, name: params.name };
}

async function cmdCookiesExport(params) {
  const details = {};
  if (params?.domain) details.domain = params.domain;
  if (params?.url) details.url = params.url;
  const cookies = await chrome.cookies.getAll(details);
  return { cookies, count: cookies.length };
}

async function cmdScreenshot(params) {
  const tabId = await resolveTabId(params);
  const tab = await chrome.tabs.get(tabId);

  // Focus WINDOW and tab for capture
  await chrome.windows.update(tab.windowId, { focused: true });
  await chrome.tabs.update(tabId, { active: true });

  // Brief delay for GPU buffer to populate after window focus
  await new Promise(r => setTimeout(r, 150));

  // Retry loop -- captureVisibleTab can fail intermittently with
  // "image readback failed" when the GPU buffer is not ready (#95)
  const maxRetries = 3;
  let dataUrl;
  for (let attempt = 1; attempt <= maxRetries; attempt++) {
    try {
      dataUrl = await chrome.tabs.captureVisibleTab(tab.windowId, {
        format: params?.type === 'jpeg' ? 'jpeg' : 'png',
        quality: params?.quality || 80,
      });
      break;
    } catch (err) {
      if (attempt === maxRetries) throw err;
      const delayMs = attempt * 500;
      await new Promise(r => setTimeout(r, delayMs));
    }
  }

  // Strip data:image/...;base64, prefix
  const base64 = dataUrl.replace(/^data:image\/\w+;base64,/, '');
  return { screenshot: base64, type: params?.type || 'png' };
}

async function cmdSnapshot(params) {
  return await forwardToContent('snapshot', params);
}

async function cmdEvaluate(params) {
  const code = params?.fn || params?.js || params?.code || '';
  if (!code.trim()) throw new Error('fn/js/code is required');

  const tabId = await resolveTabId(params);

  // Use chrome.scripting.executeScript to bypass page CSP restrictions.
  // This runs in the MAIN world so it has access to page globals.
  const results = await chrome.scripting.executeScript({
    target: { tabId },
    world: 'MAIN',
    func: (codeStr) => {
      try {
        const fn = new Function('return (' + codeStr + ')');
        let result = fn();
        if (typeof result === 'function') result = result();
        return { result };
      } catch (err) {
        return { error: err.message || String(err) };
      }
    },
    args: [code],
  });

  if (!results || !results[0]) throw new Error('No result from evaluate');
  const frame = results[0].result;
  if (frame.error) throw new Error(`Evaluate failed: ${frame.error}`);
  return { result: frame.result };
}

async function cmdPaste(params) {
  const tabId = await resolveTabId(params);
  const selector = params.selector;
  const text = params.pasteText || '';

  if (!text) throw new Error('pasteText is required');
  if (!selector) throw new Error('selector is required for paste');

  // Run in MAIN world so React/Draft.js editors see the events in their context.
  // Uses synthetic ClipboardEvent which frameworks intercept natively.
  const results = await chrome.scripting.executeScript({
    target: { tabId },
    world: 'MAIN',
    func: (sel, textToPaste, shouldClear) => {
      try {
        const element = document.querySelector(sel);
        if (!element) return { error: 'Element not found: ' + sel };

        element.focus();
        element.scrollIntoView({ behavior: 'smooth', block: 'center' });

        // Clear existing content if requested
        if (shouldClear) {
          const selection = window.getSelection();
          const range = document.createRange();
          range.selectNodeContents(element);
          selection.removeAllRanges();
          selection.addRange(range);
          document.execCommand('delete', false);
        }

        // Build clipboard data with both plain text and HTML
        const html = textToPaste.split('\n').map(l => l || '<br>').join('<br>');
        const dt = new DataTransfer();
        dt.setData('text/plain', textToPaste);
        dt.setData('text/html', html);

        // Dispatch synthetic paste event -- React/Draft.js editors intercept this
        // and update their internal state from clipboardData
        const pasteEvent = new ClipboardEvent('paste', {
          bubbles: true,
          cancelable: true,
          clipboardData: dt,
        });
        const handled = !element.dispatchEvent(pasteEvent);

        // If the editor did NOT handle the paste (did not call preventDefault),
        // use execCommand as the insertion mechanism
        if (!handled) {
          const lines = textToPaste.split('\n');
          for (let i = 0; i < lines.length; i++) {
            if (i > 0) document.execCommand('insertLineBreak', false);
            if (lines[i]) document.execCommand('insertText', false, lines[i]);
          }
        }

        // Fire input event to trigger any remaining listeners
        element.dispatchEvent(new Event('input', { bubbles: true }));

        return {
          ok: true,
          length: textToPaste.length,
          method: handled ? 'clipboardEvent' : 'execCommand',
        };
      } catch (err) {
        return { error: err.message || String(err) };
      }
    },
    args: [selector, text, params.clear !== false],
  });

  if (!results || !results[0]) throw new Error('No result from paste');
  const frame = results[0].result;
  if (frame.error) throw new Error(`Paste failed: ${frame.error}`);
  return { pasted: true, length: frame.length, method: frame.method };
}

// ---------------------------------------------------------------------------
// Content Script Communication
// ---------------------------------------------------------------------------

async function forwardToContent(command, params) {
  const tabId = await resolveTabId(params);

  // Inject content script if not already present
  try {
    await chrome.scripting.executeScript({
      target: { tabId },
      files: ['content.js'],
    });
  } catch {
    // Content script may already be injected via manifest
  }

  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      reject(new Error(`Content script timeout for "${command}" (30s)`));
    }, params?.timeout || 30000);

    chrome.tabs.sendMessage(tabId, { command, params }, (response) => {
      clearTimeout(timeout);
      if (chrome.runtime.lastError) {
        reject(new Error(chrome.runtime.lastError.message));
        return;
      }
      if (!response) {
        reject(new Error('No response from content script'));
        return;
      }
      if (response.error) {
        reject(new Error(response.error));
        return;
      }
      resolve(response.result);
    });
  });
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

async function resolveTabId(params) {
  if (params?.tabId) return parseInt(params.tabId, 10);

  // Use active tab in focused window
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab) throw new Error('No active tab found');
  return tab.id;
}

function sendToNative(msg) {
  if (!nativePort) {
    console.error('[cc-browser] Cannot send: native host not connected');
    return;
  }
  try {
    nativePort.postMessage(msg);
  } catch (err) {
    console.error('[cc-browser] Failed to send to native host:', err.message);
  }
}

// ---------------------------------------------------------------------------
// Startup
// ---------------------------------------------------------------------------

connectNative();

// Reconnect on service worker wake
chrome.runtime.onStartup.addListener(() => {
  connectNative();
});

// Handle messages from content scripts (unsolicited, e.g. page events)
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg.type === 'pageEvent') {
    sendToNative({
      id: `event-${Date.now()}`,
      event: msg.event,
      tabId: sender.tab?.id,
      data: msg.data,
    });
  }
  return false;
});
