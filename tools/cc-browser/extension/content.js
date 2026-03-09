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
    const { interactive, compact, maxDepth, maxChars = 30000, selector } = options;
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

    let rootElement = document.body;
    if (selector) {
      const scoped = document.querySelector(selector);
      if (!scoped) throw new Error(`Snapshot selector "${selector}" not found on page`);
      rootElement = scoped;
    }

    walk(rootElement, 0);

    removeNthFromNonDuplicates(refs, tracker);

    let snapshot = lines.join('\n') || (interactive ? '(no interactive elements)' : '(empty)');

    // Compact mode: remove structural-only branches
    if (compact && !interactive) {
      snapshot = compactTree(snapshot);
    }

    // Truncate (maxChars=0 means no limit)
    if (maxChars > 0 && snapshot.length > maxChars) {
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
    const textStr = String(params.inputText || params.text || '');

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

      if (element.isContentEditable) {
        // ContentEditable: clear existing content and insert new text
        // Select all existing content and delete it
        const selection = window.getSelection();
        const range = document.createRange();
        range.selectNodeContents(element);
        selection.removeAllRanges();
        selection.addRange(range);
        document.execCommand('delete', false);

        // Insert text line by line, using insertParagraph for newlines
        const lines = value.split('\n');
        for (let i = 0; i < lines.length; i++) {
          if (i > 0) {
            document.execCommand('insertParagraph', false);
          }
          if (lines[i]) {
            document.execCommand('insertText', false, lines[i]);
          }
        }

        element.dispatchEvent(new Event('input', { bubbles: true }));
      } else if ('value' in element) {
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

  function cmdPaste(params) {
    const { element, description } = resolveElement(params);
    const text = params.pasteText || params.text || params.value || '';
    const format = params.format || 'text';
    const explicitHtml = params.html || null;

    element.focus();
    element.scrollIntoView({ behavior: 'smooth', block: 'center' });

    // Select all existing content first if element has content and we want to replace
    if (params.clear !== false) {
      const selection = window.getSelection();
      const range = document.createRange();
      range.selectNodeContents(element);
      selection.removeAllRanges();
      selection.addRange(range);
      document.execCommand('delete', false);
    }

    // Build clipboard data with both plain text and HTML
    const dt = new DataTransfer();
    dt.setData('text/plain', text);

    if (format === 'html') {
      // Use explicit HTML if provided, otherwise auto-convert text to paragraphs
      const html = explicitHtml || text
        .split('\n\n')
        .map(block => '<p>' + block.replace(/\n/g, '<br>') + '</p>')
        .join('');
      dt.setData('text/html', html);
    } else {
      // Default: simple newline-to-br conversion
      const html = text.split('\n').map(line => line || '<br>').join('<br>');
      dt.setData('text/html', html);
    }

    // Dispatch synthetic paste event -- React/Ember editors intercept this
    // and update their internal state from clipboardData
    const pasteEvent = new ClipboardEvent('paste', {
      bubbles: true,
      cancelable: true,
      clipboardData: dt,
    });

    const cancelled = !element.dispatchEvent(pasteEvent);

    // If the editor handled the paste (called preventDefault), we're done.
    // If not, fall back to execCommand insertText.
    if (!cancelled) {
      document.execCommand('insertText', false, text);
    }

    element.dispatchEvent(new Event('input', { bubbles: true }));

    return {
      pasted: true,
      length: text.length,
      ref: description,
      method: cancelled ? 'clipboardEvent' : 'execCommand',
      format: format,
    };
  }

  function cmdCheck(params) {
    const { element, description } = resolveElement(params);
    const tag = element.tagName.toLowerCase();
    const type = (element.getAttribute('type') || '').toLowerCase();

    if (tag !== 'input' || (type !== 'checkbox' && type !== 'radio')) {
      throw new Error('Element "' + description + '" is not a checkbox or radio button');
    }

    if (!element.checked) {
      element.checked = true;
      element.dispatchEvent(new Event('change', { bubbles: true }));
      element.dispatchEvent(new Event('input', { bubbles: true }));
    }
    return { checked: true, ref: description, wasAlreadyChecked: element.checked };
  }

  function cmdUncheck(params) {
    const { element, description } = resolveElement(params);
    const tag = element.tagName.toLowerCase();
    const type = (element.getAttribute('type') || '').toLowerCase();

    if (tag !== 'input' || type !== 'checkbox') {
      throw new Error('Element "' + description + '" is not a checkbox');
    }

    if (element.checked) {
      element.checked = false;
      element.dispatchEvent(new Event('change', { bubbles: true }));
      element.dispatchEvent(new Event('input', { bubbles: true }));
    }
    return { unchecked: true, ref: description };
  }

  function cmdLinks(params) {
    const selector = params?.selector || 'a[href]';
    const elements = document.querySelectorAll(selector);
    const links = [];
    const seen = new Set();

    for (const el of elements) {
      const href = el.href || el.getAttribute('href') || '';
      if (!href || href === '#' || href.startsWith('javascript:')) continue;

      const key = href;
      if (params?.unique !== false && seen.has(key)) continue;
      seen.add(key);

      const text = (el.textContent || '').trim().slice(0, 200);
      const link = { href, text };

      if (params?.includeAttrs) {
        if (el.title) link.title = el.title;
        if (el.target) link.target = el.target;
        if (el.rel) link.rel = el.rel;
      }

      links.push(link);
    }

    // Filter by pattern if provided
    if (params?.pattern) {
      const re = new RegExp(params.pattern, 'i');
      return { links: links.filter(l => re.test(l.href) || re.test(l.text)), total: links.length };
    }

    return { links, total: links.length };
  }

  function cmdWaitNetworkIdle(params) {
    const idleTime = params?.idleTime || 500;
    const timeout = params?.timeout || 30000;

    return new Promise((resolve, reject) => {
      let pending = 0;
      let idleTimer = null;
      let timeoutTimer = null;

      const checkIdle = () => {
        if (pending <= 0) {
          if (idleTimer) clearTimeout(idleTimer);
          idleTimer = setTimeout(() => {
            cleanup();
            resolve({ idle: true, elapsed: Date.now() - startTime });
          }, idleTime);
        } else {
          if (idleTimer) {
            clearTimeout(idleTimer);
            idleTimer = null;
          }
        }
      };

      const startTime = Date.now();

      const observer = new PerformanceObserver((list) => {
        for (const entry of list.getEntries()) {
          if (entry.responseEnd === 0) {
            pending++;
          } else {
            pending = Math.max(0, pending - 1);
          }
        }
        checkIdle();
      });

      const cleanup = () => {
        if (timeoutTimer) clearTimeout(timeoutTimer);
        if (idleTimer) clearTimeout(idleTimer);
        try { observer.disconnect(); } catch {}
      };

      timeoutTimer = setTimeout(() => {
        cleanup();
        resolve({ idle: false, timedOut: true, pending, elapsed: Date.now() - startTime });
      }, timeout);

      try {
        observer.observe({ type: 'resource', buffered: false });
      } catch {
        // PerformanceObserver not available, just wait the idle time
        cleanup();
        setTimeout(() => {
          resolve({ idle: true, elapsed: idleTime, fallback: true });
        }, idleTime);
        return;
      }

      // Initial check - if nothing is pending, start idle timer
      checkIdle();
    });
  }

  // =========================================================================
  // User Action Recording
  // =========================================================================

  let _isRecording = false;
  let _inputDebounceTimer = null;
  const INPUT_DEBOUNCE_MS = 800;

  // Track last recorded input to avoid duplicates
  let _lastInputElement = null;
  let _lastInputValue = '';

  /**
   * Generate the best possible selector for an element.
   * Priority: id > name attr > aria-label > data-testid > stable CSS selector
   */
  function generateSelector(el) {
    if (!el || el === document.body || el === document.documentElement) return null;

    // 1. ID (if it looks stable, not auto-generated)
    if (el.id && !/^\d/.test(el.id) && !/^(ember|react|vue|ng-|:r)/.test(el.id)) {
      return { selector: `#${CSS.escape(el.id)}`, strategy: 'id' };
    }

    // 2. data-testid or data-id
    const testId = el.getAttribute('data-testid') || el.getAttribute('data-id');
    if (testId) {
      const attr = el.hasAttribute('data-testid') ? 'data-testid' : 'data-id';
      return { selector: `[${attr}="${CSS.escape(testId)}"]`, strategy: attr };
    }

    // 3. name attribute (form elements)
    if (el.name && ['INPUT', 'SELECT', 'TEXTAREA'].includes(el.tagName)) {
      const tag = el.tagName.toLowerCase();
      const type = el.getAttribute('type');
      const nameSelector = type
        ? `${tag}[name="${CSS.escape(el.name)}"][type="${type}"]`
        : `${tag}[name="${CSS.escape(el.name)}"]`;
      return { selector: nameSelector, strategy: 'name' };
    }

    // 4. aria-label
    const ariaLabel = el.getAttribute('aria-label');
    if (ariaLabel) {
      const tag = el.tagName.toLowerCase();
      return { selector: `${tag}[aria-label="${CSS.escape(ariaLabel)}"]`, strategy: 'aria-label' };
    }

    // 5. Text content for clickable elements (buttons, links)
    const text = getRecordableText(el);
    if (text) {
      return { text: text, strategy: 'text' };
    }

    // 6. Build a CSS path
    const path = buildCssPath(el);
    if (path) {
      return { selector: path, strategy: 'css-path' };
    }

    return null;
  }

  function getRecordableText(el) {
    const tag = el.tagName.toLowerCase();
    const role = el.getAttribute('role');
    const isClickable = ['a', 'button', 'summary'].includes(tag) ||
      role === 'button' || role === 'link' || role === 'tab' || role === 'menuitem';

    if (!isClickable) return null;

    const text = (el.textContent || '').trim().replace(/\s+/g, ' ');
    if (text && text.length > 0 && text.length <= 80) return text;
    return null;
  }

  function buildCssPath(el) {
    const parts = [];
    let current = el;
    let depth = 0;

    while (current && current !== document.body && depth < 5) {
      const tag = current.tagName.toLowerCase();
      let part = tag;

      // Add class names (filter out dynamic-looking ones)
      const classes = Array.from(current.classList || [])
        .filter(c => !/^(active|hover|focus|selected|open|show|hide|ng-|ember-|jsx-)/.test(c))
        .filter(c => c.length < 40)
        .slice(0, 2);

      if (classes.length > 0) {
        part += '.' + classes.map(c => CSS.escape(c)).join('.');
      }

      // Add nth-of-type if needed for uniqueness
      const parent = current.parentElement;
      if (parent) {
        const siblings = Array.from(parent.children).filter(s => {
          if (s.tagName !== current.tagName) return false;
          if (classes.length > 0 && s.classList) {
            return classes.every(c => s.classList.contains(c));
          }
          return true;
        });
        if (siblings.length > 1) {
          const idx = siblings.indexOf(current) + 1;
          part += `:nth-of-type(${idx})`;
        }
      }

      parts.unshift(part);

      // Check if this partial path is already unique
      const partialPath = parts.join(' > ');
      try {
        const matches = document.querySelectorAll(partialPath);
        if (matches.length === 1 && matches[0] === el) {
          return partialPath;
        }
      } catch { /* invalid selector, keep building */ }

      current = current.parentElement;
      depth++;
    }

    const fullPath = parts.join(' > ');
    try {
      const matches = document.querySelectorAll(fullPath);
      if (matches.length === 1) return fullPath;
    } catch { /* ignore */ }

    return fullPath;
  }

  function sendRecordedAction(action) {
    try {
      chrome.runtime.sendMessage({
        type: 'recordedAction',
        action: action,
      });
    } catch {
      // Extension context may be invalidated on navigation
    }
  }

  function flushInputDebounce() {
    if (_inputDebounceTimer) {
      clearTimeout(_inputDebounceTimer);
      _inputDebounceTimer = null;
    }
    if (_lastInputElement) {
      const el = _lastInputElement;
      const value = _lastInputValue;
      const loc = generateSelector(el);
      if (loc) {
        const action = { command: 'fill', params: {} };
        if (loc.text) action.params.text = loc.text;
        else action.params.selector = loc.selector;
        action.params.value = value;
        sendRecordedAction(action);
      }
      _lastInputElement = null;
      _lastInputValue = '';
    }
  }

  function onRecordClick(e) {
    // Flush any pending input before recording the click
    flushInputDebounce();

    const el = e.target;
    if (!el || el === document.body || el === document.documentElement) return;

    // Skip clicks on input fields (they will be captured by input events)
    const tag = el.tagName.toLowerCase();
    if (['input', 'textarea', 'select'].includes(tag)) return;

    // Find the closest meaningful clickable element
    const clickable = el.closest('a, button, [role="button"], [role="link"], [role="tab"], [role="menuitem"], summary') || el;

    const loc = generateSelector(clickable);
    if (!loc) return;

    const action = { command: 'click', params: {} };
    if (loc.text) action.params.text = loc.text;
    else action.params.selector = loc.selector;

    sendRecordedAction(action);
  }

  function onRecordInput(e) {
    const el = e.target;
    if (!el) return;

    const tag = el.tagName.toLowerCase();
    if (!['input', 'textarea'].includes(tag)) return;

    // Skip non-text inputs
    const type = (el.getAttribute('type') || 'text').toLowerCase();
    if (['checkbox', 'radio', 'file', 'submit', 'button', 'hidden', 'image'].includes(type)) return;

    // Debounce: accumulate the final value
    _lastInputElement = el;
    _lastInputValue = el.value || '';

    if (_inputDebounceTimer) clearTimeout(_inputDebounceTimer);
    _inputDebounceTimer = setTimeout(() => {
      flushInputDebounce();
    }, INPUT_DEBOUNCE_MS);
  }

  function onRecordChange(e) {
    const el = e.target;
    if (!el) return;

    const tag = el.tagName.toLowerCase();

    // Handle select changes
    if (tag === 'select') {
      const loc = generateSelector(el);
      if (!loc) return;
      const action = { command: 'select', params: {} };
      if (loc.text) action.params.text = loc.text;
      else action.params.selector = loc.selector;
      action.params.value = el.value;
      sendRecordedAction(action);
      return;
    }

    // Handle checkbox/radio changes
    const type = (el.getAttribute('type') || '').toLowerCase();
    if (tag === 'input' && (type === 'checkbox' || type === 'radio')) {
      const loc = generateSelector(el);
      if (!loc) return;
      const command = el.checked ? 'check' : 'uncheck';
      const action = { command, params: {} };
      if (loc.text) action.params.text = loc.text;
      else action.params.selector = loc.selector;
      sendRecordedAction(action);
    }
  }

  function onRecordKeydown(e) {
    // Only record special key presses (Enter, Tab, Escape)
    const specialKeys = ['Enter', 'Tab', 'Escape', 'Backspace', 'Delete'];
    if (!specialKeys.includes(e.key)) return;

    // Don't record Enter/Tab if inside a text input (captured by input handler)
    const tag = (e.target.tagName || '').toLowerCase();
    if (['input', 'textarea'].includes(tag) && e.key !== 'Escape') return;

    // Flush pending input before recording key press
    flushInputDebounce();

    sendRecordedAction({
      command: 'press',
      params: { key: e.key },
    });
  }

  function startRecording() {
    if (_isRecording) return { alreadyRecording: true };
    _isRecording = true;
    _lastInputElement = null;
    _lastInputValue = '';

    document.addEventListener('click', onRecordClick, true);
    document.addEventListener('input', onRecordInput, true);
    document.addEventListener('change', onRecordChange, true);
    document.addEventListener('keydown', onRecordKeydown, true);

    return { recording: true };
  }

  function stopRecording() {
    if (!_isRecording) return { alreadyStopped: true };

    // Flush any pending input
    flushInputDebounce();

    _isRecording = false;
    document.removeEventListener('click', onRecordClick, true);
    document.removeEventListener('input', onRecordInput, true);
    document.removeEventListener('change', onRecordChange, true);
    document.removeEventListener('keydown', onRecordKeydown, true);

    return { stopped: true };
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
          case 'check':
            result = cmdCheck(params);
            break;
          case 'uncheck':
            result = cmdUncheck(params);
            break;
          case 'paste':
            result = cmdPaste(params);
            break;
          case 'links':
            result = cmdLinks(params);
            break;
          case 'waitNetworkIdle':
            result = await cmdWaitNetworkIdle(params);
            break;
          case 'record.start':
            result = startRecording();
            break;
          case 'record.stop':
            result = stopRecording();
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
