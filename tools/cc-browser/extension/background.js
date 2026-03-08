// CC Browser v2 - Extension Background Service Worker
// Routes commands between native messaging host and content scripts.

const NATIVE_HOST_NAME = 'com.cc_browser.bridge';

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

let nativePort = null;
const pendingRequests = new Map(); // id -> { resolve, reject, timer }

// Network request log buffer
const networkLog = [];
const MAX_NETWORK_LOG = 500;

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
      case 'dialog.accept':
        result = await cmdDialogAccept(params);
        break;
      case 'dialog.dismiss':
        result = await cmdDialogDismiss(params);
        break;
      case 'console.start':
        result = await cmdConsoleStart(params);
        break;
      case 'console':
        result = await cmdConsoleRead(params);
        break;
      case 'console.clear':
        result = await cmdConsoleClear(params);
        break;
      case 'network':
        result = cmdNetwork(params);
        break;
      case 'state.save':
        result = await cmdStateSave(params);
        break;
      case 'state.load':
        result = await cmdStateLoad(params);
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
      case 'check':
      case 'uncheck':
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
  // Full-page screenshot uses CDP via chrome.debugger
  if (params?.fullPage) {
    return cmdScreenshotFullPage(params);
  }

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
  const format = params.format || 'text';
  const explicitHtml = params.html || null;

  if (!text && !explicitHtml) throw new Error('pasteText is required');
  if (!selector) throw new Error('selector is required for paste');

  // Run in MAIN world so React/Draft.js editors see the events in their context.
  // Uses synthetic ClipboardEvent which frameworks intercept natively.
  const results = await chrome.scripting.executeScript({
    target: { tabId },
    world: 'MAIN',
    func: (sel, textToPaste, shouldClear, fmt, htmlContent) => {
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
        const dt = new DataTransfer();
        dt.setData('text/plain', textToPaste);

        if (fmt === 'html') {
          // Use explicit HTML if provided, otherwise auto-convert text to paragraphs
          const html = htmlContent || textToPaste
            .split('\n\n')
            .map(block => '<p>' + block.replace(/\n/g, '<br>') + '</p>')
            .join('');
          dt.setData('text/html', html);
        } else {
          // Default: simple newline-to-br conversion
          const html = textToPaste.split('\n').map(l => l || '<br>').join('<br>');
          dt.setData('text/html', html);
        }

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
          format: fmt,
        };
      } catch (err) {
        return { error: err.message || String(err) };
      }
    },
    args: [selector, text, params.clear !== false, format, explicitHtml],
  });

  if (!results || !results[0]) throw new Error('No result from paste');
  const frame = results[0].result;
  if (frame.error) throw new Error(`Paste failed: ${frame.error}`);
  return { pasted: true, length: frame.length, method: frame.method, format: frame.format };
}

// ---------------------------------------------------------------------------
// Chrome DevTools Protocol Helper
// ---------------------------------------------------------------------------

function sendDebuggerCommand(tabId, method, commandParams) {
  return new Promise((resolve, reject) => {
    chrome.debugger.sendCommand({ tabId }, method, commandParams || {}, (result) => {
      if (chrome.runtime.lastError) {
        reject(new Error(chrome.runtime.lastError.message));
      } else {
        resolve(result);
      }
    });
  });
}

// ---------------------------------------------------------------------------
// Dialog Handling (via Chrome DevTools Protocol)
// ---------------------------------------------------------------------------

async function cmdDialogAccept(params) {
  const tabId = await resolveTabId(params);

  try {
    await chrome.debugger.attach({ tabId }, '1.3');
  } catch (err) {
    if (!err.message.includes('Already attached')) throw err;
  }

  try {
    await sendDebuggerCommand(tabId, 'Page.enable');
    await sendDebuggerCommand(tabId, 'Page.handleJavaScriptDialog', {
      accept: true,
      promptText: params?.text || '',
    });
    return { accepted: true, promptText: params?.text || null };
  } finally {
    try { await chrome.debugger.detach({ tabId }); } catch {}
  }
}

async function cmdDialogDismiss(params) {
  const tabId = await resolveTabId(params);

  try {
    await chrome.debugger.attach({ tabId }, '1.3');
  } catch (err) {
    if (!err.message.includes('Already attached')) throw err;
  }

  try {
    await sendDebuggerCommand(tabId, 'Page.enable');
    await sendDebuggerCommand(tabId, 'Page.handleJavaScriptDialog', {
      accept: false,
    });
    return { dismissed: true };
  } finally {
    try { await chrome.debugger.detach({ tabId }); } catch {}
  }
}

// ---------------------------------------------------------------------------
// Console Capture (via MAIN world script injection)
// ---------------------------------------------------------------------------

async function cmdConsoleStart(params) {
  const tabId = await resolveTabId(params);

  const results = await chrome.scripting.executeScript({
    target: { tabId },
    world: 'MAIN',
    func: () => {
      if (window.__ccConsoleCapture) return { alreadyActive: true };
      window.__ccConsoleCapture = true;

      var captured = [];
      var MAX = 500;
      var orig = {};

      ['log', 'warn', 'error', 'info', 'debug'].forEach(function(level) {
        orig[level] = console[level].bind(console);
        console[level] = function() {
          var args = Array.prototype.slice.call(arguments);
          captured.push({
            level: level,
            text: args.map(function(a) {
              try { return typeof a === 'object' ? JSON.stringify(a) : String(a); }
              catch(e) { return String(a); }
            }).join(' '),
            timestamp: Date.now(),
          });
          if (captured.length > MAX) captured.splice(0, captured.length - MAX);
          orig[level].apply(console, args);
        };
      });

      window.addEventListener('error', function(e) {
        captured.push({
          level: 'error',
          text: 'Uncaught: ' + (e.message || '') + ' at ' + (e.filename || '') + ':' + (e.lineno || ''),
          timestamp: Date.now(),
        });
      });

      window.__ccConsoleLogs = captured;
      return { started: true };
    },
  });

  if (!results || !results[0]) throw new Error('Failed to inject console capture');
  return results[0].result;
}

async function cmdConsoleRead(params) {
  const tabId = await resolveTabId(params);
  const level = params?.level || null;

  const results = await chrome.scripting.executeScript({
    target: { tabId },
    world: 'MAIN',
    func: (filterLevel) => {
      var logs = window.__ccConsoleLogs;
      if (!logs) return { error: 'Console capture not started. Run console.start first.' };
      var messages = filterLevel ? logs.filter(function(m) { return m.level === filterLevel; }) : logs.slice();
      return { messages: messages, total: logs.length };
    },
    args: [level],
  });

  if (!results || !results[0]) throw new Error('Failed to read console logs');
  const frame = results[0].result;
  if (frame.error) throw new Error(frame.error);
  return frame;
}

async function cmdConsoleClear(params) {
  const tabId = await resolveTabId(params);

  const results = await chrome.scripting.executeScript({
    target: { tabId },
    world: 'MAIN',
    func: () => {
      var logs = window.__ccConsoleLogs;
      if (!logs) return { error: 'Console capture not started.' };
      logs.length = 0;
      return { cleared: true };
    },
  });

  if (!results || !results[0]) throw new Error('Failed to clear console');
  const frame = results[0].result;
  if (frame.error) throw new Error(frame.error);
  return frame;
}

// ---------------------------------------------------------------------------
// Network Request Log (via chrome.webRequest)
// ---------------------------------------------------------------------------

chrome.webRequest.onCompleted.addListener(
  (details) => {
    networkLog.push({
      url: details.url,
      method: details.method,
      status: details.statusCode,
      type: details.type,
      tabId: details.tabId,
      timestamp: Date.now(),
    });
    if (networkLog.length > MAX_NETWORK_LOG) {
      networkLog.splice(0, networkLog.length - MAX_NETWORK_LOG);
    }
  },
  { urls: ['<all_urls>'] }
);

chrome.webRequest.onErrorOccurred.addListener(
  (details) => {
    networkLog.push({
      url: details.url,
      method: details.method,
      status: 0,
      error: details.error,
      type: details.type,
      tabId: details.tabId,
      timestamp: Date.now(),
    });
    if (networkLog.length > MAX_NETWORK_LOG) {
      networkLog.splice(0, networkLog.length - MAX_NETWORK_LOG);
    }
  },
  { urls: ['<all_urls>'] }
);

function cmdNetwork(params) {
  let entries = networkLog.slice();

  // Filter by tab
  if (params?.tabId) {
    const tid = parseInt(params.tabId, 10);
    entries = entries.filter(e => e.tabId === tid);
  }

  // Filter by URL pattern
  if (params?.filter) {
    const re = new RegExp(params.filter, 'i');
    entries = entries.filter(e => re.test(e.url));
  }

  // Filter by resource type
  if (params?.type) {
    entries = entries.filter(e => e.type === params.type);
  }

  const limit = params?.limit || 100;
  const recent = entries.slice(-limit);

  return { requests: recent, total: entries.length };
}

// ---------------------------------------------------------------------------
// State Save / Load (cookies + localStorage + sessionStorage)
// ---------------------------------------------------------------------------

async function cmdStateSave(params) {
  const tabId = await resolveTabId(params);
  const tab = await chrome.tabs.get(tabId);

  // Get cookies for current page's domain
  const url = new URL(tab.url);
  const cookies = await chrome.cookies.getAll({ domain: url.hostname });

  // Get localStorage and sessionStorage via MAIN world
  const storageResults = await chrome.scripting.executeScript({
    target: { tabId },
    world: 'MAIN',
    func: () => {
      var local = {};
      for (var i = 0; i < localStorage.length; i++) {
        var key = localStorage.key(i);
        local[key] = localStorage.getItem(key);
      }
      var session = {};
      for (var j = 0; j < sessionStorage.length; j++) {
        var skey = sessionStorage.key(j);
        session[skey] = sessionStorage.getItem(skey);
      }
      return { localStorage: local, sessionStorage: session };
    },
  });

  const storage = storageResults?.[0]?.result || {};

  return {
    url: tab.url,
    cookies: cookies.map(c => ({
      name: c.name, value: c.value, domain: c.domain,
      path: c.path, secure: c.secure, httpOnly: c.httpOnly,
      expirationDate: c.expirationDate, sameSite: c.sameSite,
    })),
    localStorage: storage.localStorage || {},
    sessionStorage: storage.sessionStorage || {},
    savedAt: new Date().toISOString(),
  };
}

async function cmdStateLoad(params) {
  const tabId = await resolveTabId(params);
  const state = params?.state;
  if (!state) throw new Error('state object is required');

  let cookiesRestored = 0;

  // Restore cookies
  if (state.cookies && Array.isArray(state.cookies)) {
    for (const c of state.cookies) {
      try {
        const cookieUrl = 'http' + (c.secure ? 's' : '') + '://' + (c.domain || '').replace(/^\./, '') + (c.path || '/');
        await chrome.cookies.set({
          url: cookieUrl,
          name: c.name,
          value: c.value,
          domain: c.domain,
          path: c.path || '/',
          secure: c.secure || false,
          httpOnly: c.httpOnly || false,
          expirationDate: c.expirationDate,
          sameSite: c.sameSite || 'unspecified',
        });
        cookiesRestored++;
      } catch (err) {
        console.log('[cc-browser] Failed to restore cookie ' + c.name + ': ' + err.message);
      }
    }
  }

  // Restore storage
  const storageResults = await chrome.scripting.executeScript({
    target: { tabId },
    world: 'MAIN',
    func: (localData, sessionData) => {
      var localCount = 0, sessionCount = 0;
      if (localData) {
        for (var key in localData) {
          if (localData.hasOwnProperty(key)) {
            localStorage.setItem(key, localData[key]);
            localCount++;
          }
        }
      }
      if (sessionData) {
        for (var skey in sessionData) {
          if (sessionData.hasOwnProperty(skey)) {
            sessionStorage.setItem(skey, sessionData[skey]);
            sessionCount++;
          }
        }
      }
      return { localStorage: localCount, sessionStorage: sessionCount };
    },
    args: [state.localStorage || null, state.sessionStorage || null],
  });

  const storageResult = storageResults?.[0]?.result || {};

  return {
    restored: true,
    cookies: cookiesRestored,
    localStorage: storageResult.localStorage || 0,
    sessionStorage: storageResult.sessionStorage || 0,
  };
}

// ---------------------------------------------------------------------------
// Full-Page Screenshot (via Chrome DevTools Protocol)
// ---------------------------------------------------------------------------

async function cmdScreenshotFullPage(params) {
  const tabId = await resolveTabId(params);
  const tab = await chrome.tabs.get(tabId);

  // Focus window and tab
  await chrome.windows.update(tab.windowId, { focused: true });
  await chrome.tabs.update(tabId, { active: true });
  await new Promise(r => setTimeout(r, 150));

  try {
    await chrome.debugger.attach({ tabId }, '1.3');
  } catch (err) {
    if (!err.message.includes('Already attached')) throw err;
  }

  try {
    // Get page dimensions via CDP
    const layoutMetrics = await sendDebuggerCommand(tabId, 'Page.getLayoutMetrics');
    const contentSize = layoutMetrics.cssContentSize || layoutMetrics.contentSize;
    const width = Math.ceil(contentSize.width);
    const height = Math.ceil(contentSize.height);

    // Capture full page screenshot
    const captureResult = await sendDebuggerCommand(tabId, 'Page.captureScreenshot', {
      format: params?.type === 'jpeg' ? 'jpeg' : 'png',
      quality: params?.type === 'jpeg' ? (params?.quality || 80) : undefined,
      captureBeyondViewport: true,
      clip: { x: 0, y: 0, width, height, scale: 1 },
    });

    return {
      screenshot: captureResult.data,
      type: params?.type || 'png',
      fullPage: true,
      width,
      height,
    };
  } finally {
    try { await chrome.debugger.detach({ tabId }); } catch {}
  }
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
