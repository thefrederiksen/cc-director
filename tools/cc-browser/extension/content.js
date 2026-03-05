// CC Browser v2 - Content Script
// Runs in every page. Handles ARIA snapshots and DOM interactions.
// Communicates with background.js via chrome.runtime.onMessage.

(() => {
  // Prevent double injection
  if (window.__ccBrowserContentLoaded) return;
  window.__ccBrowserContentLoaded = true;

  // =========================================================================
  // ARIA Snapshot Constants (ported from cc-browser-archived/src/snapshot.mjs)
  // =========================================================================

  const INTERACTIVE_ROLES = new Set([
    'button', 'link', 'textbox', 'checkbox', 'radio', 'combobox', 'listbox',
    'menuitem', 'menuitemcheckbox', 'menuitemradio', 'option', 'searchbox',
    'slider', 'spinbutton', 'switch', 'tab', 'treeitem',
  ]);

  const CONTENT_ROLES = new Set([
    'heading', 'cell', 'gridcell', 'columnheader', 'rowheader',
    'listitem', 'article', 'region', 'main', 'navigation',
  ]);

  const STRUCTURAL_ROLES = new Set([
    'generic', 'group', 'list', 'table', 'row', 'rowgroup', 'grid',
    'treegrid', 'menu', 'menubar', 'toolbar', 'tablist', 'tree',
    'directory', 'document', 'application', 'presentation', 'none',
  ]);

  // =========================================================================
  // Ref Map: ref string -> HTMLElement
  // =========================================================================

  const refMap = new Map();

  // =========================================================================
  // Human-like Delay Utilities (ported from human-mode.mjs)
  // =========================================================================

  function sleep(ms) {
    return new Promise(resolve => setTimeout(resolve, Math.max(0, Math.floor(ms))));
  }

  function randomInt(min, max) {
    min = Math.ceil(min);
    max = Math.floor(max);
    return Math.floor(Math.random() * (max - min + 1)) + min;
  }

  function gaussianRandom(mean, stddev) {
    let u = 0, v = 0;
    while (u === 0) u = Math.random();
    while (v === 0) v = Math.random();
    const z = Math.sqrt(-2.0 * Math.log(u)) * Math.cos(2.0 * Math.PI * v);
    return mean + z * stddev;
  }

  function interKeyDelay() {
    return Math.max(30, Math.min(250, Math.round(gaussianRandom(100, 40))));
  }

  function typingDelays(text) {
    const delays = [];
    for (let i = 0; i < text.length; i++) {
      delays.push(interKeyDelay());
    }
    return delays;
  }

  // =========================================================================
  // Role Name Tracker (ported from snapshot.mjs)
  // =========================================================================

  function createRoleNameTracker() {
    const counts = new Map();
    const refsByKey = new Map();

    return {
      getKey(role, name) {
        return `${role}:${name || ''}`;
      },
      getNextIndex(role, name) {
        const key = this.getKey(role, name);
        const current = counts.get(key) || 0;
        counts.set(key, current + 1);
        return current;
      },
      trackRef(role, name, ref) {
        const key = this.getKey(role, name);
        const list = refsByKey.get(key) || [];
        list.push(ref);
        refsByKey.set(key, list);
      },
      getDuplicateKeys() {
        const out = new Set();
        for (const [key, refs] of refsByKey) {
          if (refs.length > 1) out.add(key);
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
        delete data.nth;
      }
    }
  }

  // =========================================================================
  // ARIA Snapshot: DOM Walker
  // =========================================================================

  function getElementRole(el) {
    // Explicit ARIA role
    const explicitRole = el.getAttribute('role');
    if (explicitRole) return explicitRole.toLowerCase().trim();

    // Implicit roles from tag names
    const tag = el.tagName.toLowerCase();
    const implicitRoles = {
      a: el.hasAttribute('href') ? 'link' : null,
      button: 'button',
      input: getInputRole(el),
      select: 'combobox',
      textarea: 'textbox',
      h1: 'heading', h2: 'heading', h3: 'heading',
      h4: 'heading', h5: 'heading', h6: 'heading',
      img: 'img',
      nav: 'navigation',
      main: 'main',
      header: 'banner',
      footer: 'contentinfo',
      aside: 'complementary',
      section: el.getAttribute('aria-label') || el.getAttribute('aria-labelledby') ? 'region' : null,
      form: 'form',
      table: 'table',
      thead: 'rowgroup', tbody: 'rowgroup', tfoot: 'rowgroup',
      tr: 'row',
      th: 'columnheader',
      td: 'cell',
      ul: 'list', ol: 'list',
      li: 'listitem',
      article: 'article',
      dialog: 'dialog',
      details: 'group',
      summary: 'button',
      menu: 'menu',
      option: 'option',
    };

    return implicitRoles[tag] || null;
  }

  function getInputRole(el) {
    const type = (el.getAttribute('type') || 'text').toLowerCase();
    const typeRoles = {
      button: 'button', submit: 'button', reset: 'button', image: 'button',
      checkbox: 'checkbox', radio: 'radio', range: 'slider',
      search: 'searchbox', number: 'spinbutton',
    };
    return typeRoles[type] || 'textbox';
  }

  function getAccessibleName(el) {
    // aria-label takes priority
    const ariaLabel = el.getAttribute('aria-label');
    if (ariaLabel) return ariaLabel.trim();

    // aria-labelledby
    const labelledBy = el.getAttribute('aria-labelledby');
    if (labelledBy) {
      const parts = labelledBy.split(/\s+/).map(id => {
        const labelEl = document.getElementById(id);
        return labelEl ? labelEl.textContent.trim() : '';
      }).filter(Boolean);
      if (parts.length) return parts.join(' ');
    }

    // Title attribute
    const title = el.getAttribute('title');
    if (title) return title.trim();

    // Alt text for images
    if (el.tagName === 'IMG' && el.alt) return el.alt.trim();

    // Placeholder for inputs
    if ((el.tagName === 'INPUT' || el.tagName === 'TEXTAREA') && el.placeholder) {
      return el.placeholder.trim();
    }

    // Label element for form controls
    if (el.id) {
      const label = document.querySelector(`label[for="${CSS.escape(el.id)}"]`);
      if (label) return label.textContent.trim();
    }

    // Direct text content (for buttons, links, etc.)
    const tag = el.tagName.toLowerCase();
    if (['button', 'a', 'summary', 'option'].includes(tag) ||
        el.getAttribute('role') === 'button' || el.getAttribute('role') === 'link') {
      const text = getDirectTextContent(el);
      if (text) return text;
    }

    return null;
  }

  function getDirectTextContent(el) {
    let text = '';
    for (const child of el.childNodes) {
      if (child.nodeType === Node.TEXT_NODE) {
        text += child.textContent;
      } else if (child.nodeType === Node.ELEMENT_NODE) {
        // Include text from inline children but not block elements
        const display = getComputedStyle(child).display;
        if (display === 'inline' || display === 'inline-block') {
          text += child.textContent;
        }
      }
    }
    return text.trim().replace(/\s+/g, ' ').substring(0, 200) || null;
  }

  function isVisible(el) {
    if (!el.offsetParent && el.tagName !== 'BODY' && el.tagName !== 'HTML') {
      // Could be position:fixed or hidden
      const style = getComputedStyle(el);
      if (style.display === 'none' || style.visibility === 'hidden') return false;
      if (style.position !== 'fixed' && style.position !== 'sticky') return false;
    }
    const style = getComputedStyle(el);
    if (style.display === 'none' || style.visibility === 'hidden') return false;
    if (parseFloat(style.opacity) === 0) return false;
    return true;
  }

  function buildSnapshot(options = {}) {
    const { interactive, compact, maxDepth, maxChars = 10000 } = options;
    refMap.clear();

    const tracker = createRoleNameTracker();
    const refs = {};
    let counter = 0;
    const nextRef = () => `e${++counter}`;

    const lines = [];

    function walk(el, depth) {
      if (maxDepth !== undefined && depth > maxDepth) return;
      if (!isVisible(el)) return;

      const role = getElementRole(el);
      const name = getAccessibleName(el);
      const indent = '  '.repeat(depth);

      if (role) {
        const isInteractive = INTERACTIVE_ROLES.has(role);
        const isContent = CONTENT_ROLES.has(role);
        const isStructural = STRUCTURAL_ROLES.has(role);

        if (interactive && !isInteractive) {
          // In interactive mode, still walk children
          for (const child of el.children) walk(child, depth);
          return;
        }

        if (compact && isStructural && !name) {
          for (const child of el.children) walk(child, depth);
          return;
        }

        const shouldHaveRef = isInteractive || (isContent && name);

        if (shouldHaveRef) {
          const ref = nextRef();
          const nth = tracker.getNextIndex(role, name);
          tracker.trackRef(role, name, ref);
          refs[ref] = { role, name, nth };
          refMap.set(ref, el);

          let line = `${indent}- ${role}`;
          if (name) line += ` "${name}"`;
          line += ` [ref=${ref}]`;
          if (nth > 0) line += ` [nth=${nth}]`;

          // Add state info
          if (role === 'checkbox' || role === 'switch') {
            line += el.checked ? ' [checked]' : '';
          }
          if (role === 'textbox' || role === 'searchbox' || role === 'combobox') {
            const val = (el.value || '').substring(0, 50);
            if (val) line += ` [value="${val}"]`;
          }

          lines.push(line);
        } else if (!interactive) {
          let line = `${indent}- ${role}`;
          if (name) line += ` "${name}"`;
          lines.push(line);
        }
      }

      // Walk children
      for (const child of el.children) {
        walk(child, depth + (role ? 1 : 0));
      }

      // Add text content nodes for leaf elements without roles
      if (!role && el.children.length === 0) {
        const text = el.textContent.trim().replace(/\s+/g, ' ');
        if (text && text.length > 0 && text.length < 200) {
          // Only include meaningful text nodes
          const parent = el.parentElement;
          const parentRole = parent ? getElementRole(parent) : null;
          if (parentRole && !STRUCTURAL_ROLES.has(parentRole)) {
            lines.push(`${indent}- text "${text.substring(0, 100)}"`);
          }
        }
      }
    }

    walk(document.body, 0);

    removeNthFromNonDuplicates(refs, tracker);

    let snapshot = lines.join('\n') || (interactive ? '(no interactive elements)' : '(empty)');

    // Compact mode: remove structural-only branches
    if (compact && !interactive) {
      snapshot = compactTree(snapshot);
    }

    // Truncate
    if (maxChars && snapshot.length > maxChars) {
      snapshot = snapshot.substring(0, maxChars) + '\n... (truncated)';
    }

    return {
      snapshot,
      refs,
      stats: {
        chars: snapshot.length,
        lines: lines.length,
        refs: Object.keys(refs).length,
        interactive: Object.values(refs).filter(r => INTERACTIVE_ROLES.has(r.role)).length,
      },
    };
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

      const currentIndent = Math.floor((line.match(/^(\s*)/)?.[1]?.length || 0) / 2);
      let hasRelevantChildren = false;
      for (let j = i + 1; j < lines.length; j++) {
        const childIndent = Math.floor((lines[j].match(/^(\s*)/)?.[1]?.length || 0) / 2);
        if (childIndent <= currentIndent) break;
        if (lines[j].includes('[ref=')) {
          hasRelevantChildren = true;
          break;
        }
      }
      if (hasRelevantChildren) result.push(line);
    }

    return result.join('\n');
  }

  // =========================================================================
  // Element Resolution (ported from interactions.mjs resolveLocator)
  // =========================================================================

  function resolveElement(params) {
    const { ref, text, selector, exact } = params || {};
    const hasRef = ref && String(ref).trim();
    const hasText = text && String(text).trim();
    const hasSelector = selector && String(selector).trim();

    const count = (hasRef ? 1 : 0) + (hasText ? 1 : 0) + (hasSelector ? 1 : 0);
    if (count === 0) throw new Error('One of ref, text, or selector is required');
    if (count > 1) throw new Error('Only one of ref, text, or selector can be used');

    if (hasRef) {
      const refStr = String(ref).trim();
      const el = refMap.get(refStr);
      if (!el) throw new Error(`Element "${refStr}" not found. Run a new snapshot.`);
      if (!document.contains(el)) throw new Error(`Element "${refStr}" is no longer attached. Run a new snapshot.`);
      return { element: el, description: refStr };
    }

    if (hasText) {
      const textStr = String(text).trim();
      const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
      let node;
      while ((node = walker.nextNode())) {
        const content = node.textContent.trim();
        const match = exact ? content === textStr : content.includes(textStr);
        if (match && node.parentElement) {
          return { element: node.parentElement, description: `text="${textStr}"` };
        }
      }
      throw new Error(`No element found with text "${textStr}"`);
    }

    const selectorStr = String(selector).trim();
    const el = document.querySelector(selectorStr);
    if (!el) throw new Error(`No element found for selector "${selectorStr}"`);
    return { element: el, description: `selector="${selectorStr}"` };
  }

  function toAIFriendlyError(err, ref) {
    const msg = err?.message || String(err);

    if (msg.includes('not found') || msg.includes('no longer attached')) {
      return new Error(
        `Element "${ref}" not found or no longer attached. Run a new snapshot to get updated refs.`
      );
    }

    return new Error(`Action failed on "${ref}": ${msg}`);
  }

  // =========================================================================
  // DOM Interaction Commands
  // =========================================================================

  function cmdClick(params) {
    const { element, description } = resolveElement(params);
    try {
      element.scrollIntoView({ behavior: 'smooth', block: 'center' });
      if (params.doubleClick) {
        element.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, cancelable: true }));
      } else {
        element.click();
      }
      return { clicked: description };
    } catch (err) {
      throw toAIFriendlyError(err, description);
    }
  }

  function cmdDblClick(params) {
    return cmdClick({ ...params, doubleClick: true });
  }

  function cmdHover(params) {
    const { element, description } = resolveElement(params);
    try {
      element.scrollIntoView({ behavior: 'smooth', block: 'center' });
      element.dispatchEvent(new MouseEvent('mouseenter', { bubbles: true }));
      element.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }));
      return { hovered: description };
    } catch (err) {
      throw toAIFriendlyError(err, description);
    }
  }

  async function cmdType(params) {
    const { element, description } = resolveElement(params);
    const textStr = String(params.text || '');

    try {
      element.scrollIntoView({ behavior: 'smooth', block: 'center' });
      element.focus();

      // Type character by character with human-like delays
      const delays = typingDelays(textStr);
      for (let i = 0; i < textStr.length; i++) {
        const ch = textStr[i];
        element.dispatchEvent(new KeyboardEvent('keydown', { key: ch, bubbles: true }));
        element.dispatchEvent(new KeyboardEvent('keypress', { key: ch, bubbles: true }));

        // Update value for input/textarea
        if ('value' in element) {
          element.value += ch;
          element.dispatchEvent(new Event('input', { bubbles: true }));
        } else {
          // contentEditable
          document.execCommand('insertText', false, ch);
        }

        element.dispatchEvent(new KeyboardEvent('keyup', { key: ch, bubbles: true }));

        if (i < textStr.length - 1) {
          await sleep(delays[i]);
        }
      }

      if (params.submit) {
        await sleep(randomInt(100, 300));
        element.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', bubbles: true }));
        element.dispatchEvent(new KeyboardEvent('keypress', { key: 'Enter', code: 'Enter', bubbles: true }));
        element.dispatchEvent(new KeyboardEvent('keyup', { key: 'Enter', code: 'Enter', bubbles: true }));

        // Submit the form if there is one
        const form = element.closest('form');
        if (form) form.requestSubmit();
      }

      return { typed: textStr, ref: description };
    } catch (err) {
      throw toAIFriendlyError(err, description);
    }
  }

  function cmdFill(params) {
    const { element, description } = resolveElement(params);
    const value = String(params.value || params.text || '');

    try {
      element.scrollIntoView({ behavior: 'smooth', block: 'center' });
      element.focus();

      if ('value' in element) {
        // Native input setter to trigger React/Vue change detection
        const nativeInputValueSetter = Object.getOwnPropertyDescriptor(
          HTMLInputElement.prototype, 'value'
        )?.set || Object.getOwnPropertyDescriptor(
          HTMLTextAreaElement.prototype, 'value'
        )?.set;

        if (nativeInputValueSetter) {
          nativeInputValueSetter.call(element, value);
        } else {
          element.value = value;
        }

        element.dispatchEvent(new Event('input', { bubbles: true }));
        element.dispatchEvent(new Event('change', { bubbles: true }));
      }

      return { filled: description, value };
    } catch (err) {
      throw toAIFriendlyError(err, description);
    }
  }

  function cmdPress(params) {
    const key = String(params.key || '').trim();
    if (!key) throw new Error('key is required');

    const target = params.ref ? resolveElement(params).element : document.activeElement || document.body;

    target.dispatchEvent(new KeyboardEvent('keydown', {
      key, code: key, bubbles: true, cancelable: true,
    }));
    target.dispatchEvent(new KeyboardEvent('keypress', {
      key, code: key, bubbles: true, cancelable: true,
    }));
    target.dispatchEvent(new KeyboardEvent('keyup', {
      key, code: key, bubbles: true, cancelable: true,
    }));

    return { pressed: key };
  }

  function cmdSelect(params) {
    const { element, description } = resolveElement(params);
    const value = params.value || params.values;

    if (element.tagName === 'SELECT') {
      const values = Array.isArray(value) ? value : [value];
      for (const opt of element.options) {
        opt.selected = values.includes(opt.value) || values.includes(opt.textContent.trim());
      }
      element.dispatchEvent(new Event('change', { bubbles: true }));
      return { selected: values, ref: description };
    }

    throw new Error(`Element "${description}" is not a <select>`);
  }

  function cmdScroll(params) {
    if (params.ref) {
      const { element, description } = resolveElement(params);
      element.scrollIntoView({ behavior: 'smooth', block: 'center' });
      return { scrolled: description };
    }

    const amount = params.amount || 500;
    const direction = String(params.direction || 'down').toLowerCase();

    let deltaX = 0, deltaY = 0;
    switch (direction) {
      case 'up': deltaY = -amount; break;
      case 'down': deltaY = amount; break;
      case 'left': deltaX = -amount; break;
      case 'right': deltaX = amount; break;
      default: deltaY = amount;
    }

    window.scrollBy({ left: deltaX, top: deltaY, behavior: 'smooth' });
    return { scrolled: direction, amount };
  }

  function cmdDrag(params) {
    const startEl = resolveElement({ ref: params.startRef || params.from }).element;
    const endEl = resolveElement({ ref: params.endRef || params.to }).element;

    const startRect = startEl.getBoundingClientRect();
    const endRect = endEl.getBoundingClientRect();

    const startX = startRect.left + startRect.width / 2;
    const startY = startRect.top + startRect.height / 2;
    const endX = endRect.left + endRect.width / 2;
    const endY = endRect.top + endRect.height / 2;

    startEl.dispatchEvent(new DragEvent('dragstart', {
      bubbles: true, clientX: startX, clientY: startY,
    }));
    endEl.dispatchEvent(new DragEvent('dragover', {
      bubbles: true, clientX: endX, clientY: endY,
    }));
    endEl.dispatchEvent(new DragEvent('drop', {
      bubbles: true, clientX: endX, clientY: endY,
    }));
    startEl.dispatchEvent(new DragEvent('dragend', {
      bubbles: true, clientX: endX, clientY: endY,
    }));

    return { dragged: `${params.startRef || params.from} -> ${params.endRef || params.to}` };
  }

  async function cmdWait(params) {
    const timeout = params.timeout || 20000;
    const startTime = Date.now();

    if (params.time) {
      await sleep(Math.min(params.time, 30000));
      return { waited: true };
    }

    if (params.text) {
      return waitForCondition(() => {
        return document.body.textContent.includes(params.text);
      }, timeout, `text "${params.text}"`);
    }

    if (params.textGone) {
      return waitForCondition(() => {
        return !document.body.textContent.includes(params.textGone);
      }, timeout, `text gone "${params.textGone}"`);
    }

    if (params.selector) {
      return waitForCondition(() => {
        return document.querySelector(params.selector) !== null;
      }, timeout, `selector "${params.selector}"`);
    }

    if (params.url) {
      return waitForCondition(() => {
        return window.location.href.includes(params.url);
      }, timeout, `url "${params.url}"`);
    }

    return { waited: true };
  }

  async function waitForCondition(fn, timeoutMs, description) {
    const startTime = Date.now();
    while (Date.now() - startTime < timeoutMs) {
      if (fn()) return { waited: true, condition: description };
      await sleep(200);
    }
    throw new Error(`Timeout waiting for ${description} (${timeoutMs}ms)`);
  }

  function cmdEvaluate(params) {
    const code = params.fn || params.js || params.code || '';
    if (!code.trim()) throw new Error('fn/js/code is required');

    try {
      const fn = new Function('return (' + code + ')');
      let result = fn();
      if (typeof result === 'function') result = result();
      return { result };
    } catch (err) {
      throw new Error(`Evaluate failed: ${err.message}`);
    }
  }

  function cmdGetText(params) {
    if (params.ref || params.selector || params.text) {
      const { element } = resolveElement(params);
      return { text: element.textContent };
    }
    return { text: document.body.textContent };
  }

  function cmdGetHtml(params) {
    if (params.ref || params.selector || params.text) {
      const { element } = resolveElement(params);
      return { html: params.outer ? element.outerHTML : element.innerHTML };
    }
    return { html: params.outer ? document.documentElement.outerHTML : document.body.innerHTML };
  }

  function cmdGetInfo() {
    return {
      url: window.location.href,
      title: document.title,
      viewport: {
        width: window.innerWidth,
        height: window.innerHeight,
      },
    };
  }

  function cmdUpload(params) {
    const { element, description } = resolveElement(params);

    if (!params.data || !params.filename) {
      throw new Error('data (base64) and filename are required');
    }

    // Decode base64 to blob
    const binary = atob(params.data);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    const blob = new Blob([bytes], { type: params.mimeType || 'application/octet-stream' });
    const file = new File([blob], params.filename, { type: blob.type });

    const dt = new DataTransfer();
    dt.items.add(file);

    if (element.tagName === 'INPUT' && element.type === 'file') {
      element.files = dt.files;
      element.dispatchEvent(new Event('change', { bubbles: true }));
      element.dispatchEvent(new Event('input', { bubbles: true }));
    } else {
      // Drop on arbitrary element
      element.dispatchEvent(new DragEvent('drop', {
        bubbles: true, dataTransfer: dt,
      }));
    }

    return { uploaded: params.filename, ref: description };
  }

  // =========================================================================
  // Message Handler
  // =========================================================================

  chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    const { command, params } = msg;

    // Handle async commands
    const handleAsync = async () => {
      try {
        let result;
        switch (command) {
          case 'snapshot':
            result = buildSnapshot(params);
            break;
          case 'click':
            result = cmdClick(params);
            break;
          case 'dblclick':
            result = cmdDblClick(params);
            break;
          case 'hover':
            result = cmdHover(params);
            break;
          case 'type':
            result = await cmdType(params);
            break;
          case 'fill':
            result = cmdFill(params);
            break;
          case 'press':
            result = cmdPress(params);
            break;
          case 'select':
            result = cmdSelect(params);
            break;
          case 'scroll':
            result = cmdScroll(params);
            break;
          case 'drag':
            result = cmdDrag(params);
            break;
          case 'wait':
            result = await cmdWait(params);
            break;
          case 'evaluate':
            result = cmdEvaluate(params);
            break;
          case 'getText':
            result = cmdGetText(params);
            break;
          case 'getHtml':
            result = cmdGetHtml(params);
            break;
          case 'getInfo':
            result = cmdGetInfo();
            break;
          case 'upload':
            result = cmdUpload(params);
            break;
          default:
            sendResponse({ error: `Unknown content command: ${command}` });
            return;
        }
        sendResponse({ result });
      } catch (err) {
        sendResponse({ error: err.message || String(err) });
      }
    };

    handleAsync();
    return true; // Keep message channel open for async response
  });
})();
