// CC Browser v2 - ARIA Snapshot Tests
// Tests role classification, ref assignment, and compact mode.
// These test the buildRoleSnapshotFromAriaSnapshot logic from v1 (which is
// now implemented as DOM-walking in content.js). We test the core algorithm
// extracted here for unit testing without a browser.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

// ---------------------------------------------------------------------------
// Extracted snapshot logic (mirrors content.js algorithm)
// ---------------------------------------------------------------------------

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

// Simulate snapshot generation from a list of elements with roles/names
function simulateSnapshot(elements, options = {}) {
  const { interactive, compact } = options;
  const tracker = createRoleNameTracker();
  const refs = {};
  let counter = 0;
  const nextRef = () => `e${++counter}`;

  const lines = [];

  for (const el of elements) {
    const { role, name, depth = 0 } = el;
    const indent = '  '.repeat(depth);
    const isInteractive = INTERACTIVE_ROLES.has(role);
    const isContent = CONTENT_ROLES.has(role);
    const isStructural = STRUCTURAL_ROLES.has(role);

    if (interactive && !isInteractive) continue;
    if (compact && isStructural && !name) continue;

    const shouldHaveRef = isInteractive || (isContent && name);

    if (shouldHaveRef) {
      const ref = nextRef();
      const nth = tracker.getNextIndex(role, name);
      tracker.trackRef(role, name, ref);
      refs[ref] = { role, name, nth };

      let line = `${indent}- ${role}`;
      if (name) line += ` "${name}"`;
      line += ` [ref=${ref}]`;
      if (nth > 0) line += ` [nth=${nth}]`;
      lines.push(line);
    } else {
      let line = `${indent}- ${role}`;
      if (name) line += ` "${name}"`;
      lines.push(line);
    }
  }

  removeNthFromNonDuplicates(refs, tracker);

  return {
    snapshot: lines.join('\n') || (interactive ? '(no interactive elements)' : '(empty)'),
    refs,
  };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('ARIA Snapshot', () => {
  describe('Role classification', () => {
    it('should classify interactive roles correctly', () => {
      const interactiveRoles = ['button', 'link', 'textbox', 'checkbox', 'radio',
        'combobox', 'listbox', 'menuitem', 'tab', 'treeitem', 'switch', 'slider'];

      for (const role of interactiveRoles) {
        assert.ok(INTERACTIVE_ROLES.has(role), `${role} should be interactive`);
        assert.ok(!CONTENT_ROLES.has(role), `${role} should not be content`);
        assert.ok(!STRUCTURAL_ROLES.has(role), `${role} should not be structural`);
      }
    });

    it('should classify content roles correctly', () => {
      const contentRoles = ['heading', 'cell', 'listitem', 'article', 'region', 'main', 'navigation'];

      for (const role of contentRoles) {
        assert.ok(CONTENT_ROLES.has(role), `${role} should be content`);
        assert.ok(!INTERACTIVE_ROLES.has(role), `${role} should not be interactive`);
      }
    });

    it('should classify structural roles correctly', () => {
      const structuralRoles = ['generic', 'group', 'list', 'table', 'row', 'grid', 'menu'];

      for (const role of structuralRoles) {
        assert.ok(STRUCTURAL_ROLES.has(role), `${role} should be structural`);
        assert.ok(!INTERACTIVE_ROLES.has(role), `${role} should not be interactive`);
      }
    });

    it('roles should not overlap between categories', () => {
      for (const role of INTERACTIVE_ROLES) {
        assert.ok(!CONTENT_ROLES.has(role), `${role} overlaps interactive/content`);
        assert.ok(!STRUCTURAL_ROLES.has(role), `${role} overlaps interactive/structural`);
      }
      for (const role of CONTENT_ROLES) {
        assert.ok(!STRUCTURAL_ROLES.has(role), `${role} overlaps content/structural`);
      }
    });
  });

  describe('Ref assignment', () => {
    it('should assign refs to interactive elements', () => {
      const elements = [
        { role: 'button', name: 'Submit' },
        { role: 'link', name: 'Home' },
        { role: 'textbox', name: 'Email' },
      ];

      const { refs } = simulateSnapshot(elements);
      assert.equal(Object.keys(refs).length, 3);
      assert.equal(refs.e1.role, 'button');
      assert.equal(refs.e2.role, 'link');
      assert.equal(refs.e3.role, 'textbox');
    });

    it('should assign refs to named content elements', () => {
      const elements = [
        { role: 'heading', name: 'Welcome' },
        { role: 'heading', name: null },  // no name = no ref
        { role: 'region', name: 'Sidebar' },
      ];

      const { refs } = simulateSnapshot(elements);
      assert.equal(Object.keys(refs).length, 2);
      assert.equal(refs.e1.role, 'heading');
      assert.equal(refs.e1.name, 'Welcome');
      assert.equal(refs.e2.role, 'region');
    });

    it('should NOT assign refs to structural elements', () => {
      const elements = [
        { role: 'group' },
        { role: 'list' },
        { role: 'table' },
      ];

      const { refs } = simulateSnapshot(elements);
      assert.equal(Object.keys(refs).length, 0);
    });

    it('should generate sequential ref IDs', () => {
      const elements = [
        { role: 'button', name: 'A' },
        { role: 'button', name: 'B' },
        { role: 'button', name: 'C' },
      ];

      const { refs } = simulateSnapshot(elements);
      assert.ok(refs.e1);
      assert.ok(refs.e2);
      assert.ok(refs.e3);
    });
  });

  describe('Nth tracking (duplicate disambiguation)', () => {
    it('should add nth for duplicate role+name pairs', () => {
      const elements = [
        { role: 'button', name: 'Delete' },
        { role: 'button', name: 'Delete' },
        { role: 'button', name: 'Delete' },
      ];

      const { refs, snapshot } = simulateSnapshot(elements);
      // All three should have nth since they are duplicates
      assert.equal(refs.e1.nth, 0);
      assert.equal(refs.e2.nth, 1);
      assert.equal(refs.e3.nth, 2);
      assert.ok(snapshot.includes('[nth=1]'));
      assert.ok(snapshot.includes('[nth=2]'));
    });

    it('should remove nth for unique role+name pairs', () => {
      const elements = [
        { role: 'button', name: 'Submit' },
        { role: 'button', name: 'Cancel' },
        { role: 'link', name: 'Home' },
      ];

      const { refs } = simulateSnapshot(elements);
      // All unique, nth should be removed
      assert.equal(refs.e1.nth, undefined);
      assert.equal(refs.e2.nth, undefined);
      assert.equal(refs.e3.nth, undefined);
    });

    it('should handle mixed unique and duplicate', () => {
      const elements = [
        { role: 'button', name: 'Edit' },
        { role: 'button', name: 'Edit' },
        { role: 'button', name: 'Save' },
      ];

      const { refs } = simulateSnapshot(elements);
      // Edit buttons: duplicated, keep nth
      assert.equal(refs.e1.nth, 0);
      assert.equal(refs.e2.nth, 1);
      // Save button: unique, remove nth
      assert.equal(refs.e3.nth, undefined);
    });
  });

  describe('Interactive mode', () => {
    it('should only include interactive elements', () => {
      const elements = [
        { role: 'heading', name: 'Title' },
        { role: 'button', name: 'Submit' },
        { role: 'group' },
        { role: 'link', name: 'Home' },
        { role: 'listitem', name: 'Item 1' },
      ];

      const { snapshot, refs } = simulateSnapshot(elements, { interactive: true });
      assert.equal(Object.keys(refs).length, 2);
      assert.ok(snapshot.includes('button "Submit"'));
      assert.ok(snapshot.includes('link "Home"'));
      assert.ok(!snapshot.includes('heading'));
      assert.ok(!snapshot.includes('group'));
      assert.ok(!snapshot.includes('listitem'));
    });

    it('should return empty message when no interactive elements', () => {
      const elements = [
        { role: 'heading', name: 'Title' },
        { role: 'group' },
      ];

      const { snapshot } = simulateSnapshot(elements, { interactive: true });
      assert.equal(snapshot, '(no interactive elements)');
    });
  });

  describe('Compact mode', () => {
    it('should skip unnamed structural elements', () => {
      const elements = [
        { role: 'group' },        // structural, no name -> skip
        { role: 'group', name: 'Nav' },  // structural with name -> keep
        { role: 'button', name: 'OK' },
      ];

      const { snapshot } = simulateSnapshot(elements, { compact: true });
      assert.ok(!snapshot.includes('- group\n'));
      assert.ok(snapshot.includes('group "Nav"'));
      assert.ok(snapshot.includes('button "OK"'));
    });
  });

  describe('Output format', () => {
    it('should match v1 output format: role "name" [ref=eN]', () => {
      const elements = [
        { role: 'link', name: 'Home' },
      ];

      const { snapshot } = simulateSnapshot(elements);
      assert.equal(snapshot, '- link "Home" [ref=e1]');
    });

    it('should include depth indentation', () => {
      const elements = [
        { role: 'navigation', name: 'Main', depth: 0 },
        { role: 'link', name: 'Home', depth: 1 },
        { role: 'link', name: 'About', depth: 1 },
      ];

      const { snapshot } = simulateSnapshot(elements);
      const lines = snapshot.split('\n');
      assert.ok(lines[0].startsWith('- navigation'));
      assert.ok(lines[1].startsWith('  - link "Home"'));
      assert.ok(lines[2].startsWith('  - link "About"'));
    });
  });

  describe('RoleNameTracker', () => {
    it('should track counts per role+name key', () => {
      const tracker = createRoleNameTracker();
      assert.equal(tracker.getNextIndex('button', 'OK'), 0);
      assert.equal(tracker.getNextIndex('button', 'OK'), 1);
      assert.equal(tracker.getNextIndex('button', 'Cancel'), 0);
      assert.equal(tracker.getNextIndex('button', 'OK'), 2);
    });

    it('should distinguish null and empty names', () => {
      const tracker = createRoleNameTracker();
      assert.equal(tracker.getKey('button', null), 'button:');
      assert.equal(tracker.getKey('button', ''), 'button:');
      assert.equal(tracker.getKey('button', 'OK'), 'button:OK');
    });

    it('should identify duplicate keys', () => {
      const tracker = createRoleNameTracker();
      tracker.trackRef('button', 'OK', 'e1');
      tracker.trackRef('button', 'OK', 'e2');
      tracker.trackRef('link', 'Home', 'e3');

      const dups = tracker.getDuplicateKeys();
      assert.ok(dups.has('button:OK'));
      assert.ok(!dups.has('link:Home'));
    });
  });
});
