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
      case 'screenshot':
        result = await cmdScreenshot(params);
        break;
      case 'snapshot':
        result = await cmdSnapshot(params);
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
      case 'evaluate':
      case 'getText':
      case 'getHtml':
      case 'getInfo':
      case 'upload':
        result = await forwardToContent(command, params);
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
  const tabId = params?.tabId;
  if (!tabId) throw new Error('tabId is required');
  await chrome.tabs.remove(tabId);
  return { closed: tabId };
}

async function cmdTabFocus(params) {
  const tabId = params?.tabId;
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

async function cmdScreenshot(params) {
  const tabId = await resolveTabId(params);
  const tab = await chrome.tabs.get(tabId);

  // Focus the tab for capture
  await chrome.tabs.update(tabId, { active: true });

  const dataUrl = await chrome.tabs.captureVisibleTab(tab.windowId, {
    format: params?.type === 'jpeg' ? 'jpeg' : 'png',
    quality: params?.quality || 80,
  });

  // Strip data:image/...;base64, prefix
  const base64 = dataUrl.replace(/^data:image\/\w+;base64,/, '');
  return { screenshot: base64, type: params?.type || 'png' };
}

async function cmdSnapshot(params) {
  return await forwardToContent('snapshot', params);
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
  if (params?.tabId) return params.tabId;

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
