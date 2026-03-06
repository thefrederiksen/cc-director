// CC Browser v2 - Connection Registry Tests
// Tests CRUD, validation, and tool binding resolution.

import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, rmSync, existsSync, readFileSync, writeFileSync } from 'fs';
import { join } from 'path';
import { tmpdir } from 'os';

// Override LOCALAPPDATA to use temp directory for testing
const testDir = join(tmpdir(), `cc-browser-test-${Date.now()}`);
process.env.LOCALAPPDATA = testDir;

// Import after env override
const {
  listConnections, getConnection, createConnection,
  deleteConnection, setConnectionStatus, findConnectionByTool,
  getProfileDir, getConnectionsDir,
} = await import('../../src/connections.mjs');

describe('Connection Registry', () => {
  beforeEach(() => {
    mkdirSync(join(testDir, 'cc-director', 'connections'), { recursive: true });
  });

  afterEach(() => {
    try {
      rmSync(testDir, { recursive: true, force: true });
    } catch {
      // Best effort cleanup
    }
  });

  describe('createConnection', () => {
    it('should create a connection with valid name', () => {
      const conn = createConnection({ name: 'linkedin', url: 'https://linkedin.com' });
      assert.equal(conn.name, 'linkedin');
      assert.equal(conn.url, 'https://linkedin.com');
      assert.equal(conn.status, 'disconnected');
      assert.ok(conn.createdAt);
    });

    it('should reject invalid names', () => {
      assert.throws(() => createConnection({ name: 'My Connection' }), /lowercase alphanumeric/);
      assert.throws(() => createConnection({ name: 'UPPER' }), /lowercase alphanumeric/);
      assert.throws(() => createConnection({ name: 'has space' }), /lowercase alphanumeric/);
      assert.throws(() => createConnection({ name: 'has_underscore' }), /lowercase alphanumeric/);
    });

    it('should accept names with hyphens', () => {
      const conn = createConnection({ name: 'dev-studio' });
      assert.equal(conn.name, 'dev-studio');
    });

    it('should reject duplicate names', () => {
      createConnection({ name: 'test-dup' });
      assert.throws(() => createConnection({ name: 'test-dup' }), /already exists/);
    });

    it('should reject empty name', () => {
      assert.throws(() => createConnection({ name: '' }), /required/);
      assert.throws(() => createConnection({}), /required/);
    });

    it('should create profile directory', () => {
      createConnection({ name: 'profile-test' });
      const profileDir = getProfileDir('profile-test');
      assert.ok(existsSync(profileDir));
    });

    it('should store toolBinding', () => {
      const conn = createConnection({
        name: 'linked',
        url: 'https://linkedin.com',
        toolBinding: 'cc-browser',
      });
      assert.equal(conn.toolBinding, 'cc-browser');
    });
  });

  describe('listConnections', () => {
    it('should return empty array when no connections', () => {
      const list = listConnections();
      assert.ok(Array.isArray(list));
      assert.equal(list.length, 0);
    });

    it('should return all connections', () => {
      createConnection({ name: 'conn-a' });
      createConnection({ name: 'conn-b' });
      createConnection({ name: 'conn-c' });
      assert.equal(listConnections().length, 3);
    });
  });

  describe('getConnection', () => {
    it('should find existing connection', () => {
      createConnection({ name: 'findme', url: 'https://example.com' });
      const conn = getConnection('findme');
      assert.ok(conn);
      assert.equal(conn.name, 'findme');
      assert.equal(conn.url, 'https://example.com');
    });

    it('should return null for missing connection', () => {
      const conn = getConnection('nonexistent');
      assert.equal(conn, null);
    });
  });

  describe('deleteConnection', () => {
    it('should remove connection from registry', () => {
      createConnection({ name: 'deleteme' });
      assert.equal(listConnections().length, 1);
      deleteConnection('deleteme');
      assert.equal(listConnections().length, 0);
    });

    it('should throw for missing connection', () => {
      assert.throws(() => deleteConnection('nope'), /not found/);
    });
  });

  describe('setConnectionStatus', () => {
    it('should update status', () => {
      createConnection({ name: 'status-test' });
      setConnectionStatus('status-test', 'connected');
      const conn = getConnection('status-test');
      assert.equal(conn.status, 'connected');
    });
  });

  describe('findConnectionByTool', () => {
    it('should find connection by tool binding', () => {
      createConnection({ name: 'li', toolBinding: 'linkedin' });
      createConnection({ name: 'rd', toolBinding: 'cc-reddit' });

      const li = findConnectionByTool('linkedin');
      assert.ok(li);
      assert.equal(li.name, 'li');

      const rd = findConnectionByTool('cc-reddit');
      assert.ok(rd);
      assert.equal(rd.name, 'rd');
    });

    it('should return null for unbound tool', () => {
      const result = findConnectionByTool('cc-unknown');
      assert.equal(result, null);
    });
  });

  describe('getProfileDir', () => {
    it('should return path under connections directory', () => {
      const dir = getProfileDir('test-profile');
      assert.ok(dir.includes('connections'));
      assert.ok(dir.includes('test-profile'));
    });
  });
});
