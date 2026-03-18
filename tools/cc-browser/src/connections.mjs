// CC Browser v2 - Connection Registry
// CRUD operations for browser connections stored in connections.json.

import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'fs';
import { join } from 'path';
import { homedir } from 'os';

// ---------------------------------------------------------------------------
// Paths
// ---------------------------------------------------------------------------

function getConnectionsDir() {
  const localAppData = process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local');
  return join(localAppData, 'cc-director', 'connections');
}

function getRegistryPath() {
  return join(getConnectionsDir(), 'connections.json');
}

function ensureDir(dir) {
  if (!existsSync(dir)) mkdirSync(dir, { recursive: true });
  return dir;
}

// ---------------------------------------------------------------------------
// Registry I/O
// ---------------------------------------------------------------------------

function readRegistry() {
  const path = getRegistryPath();
  if (!existsSync(path)) return [];

  try {
    const data = JSON.parse(readFileSync(path, 'utf8'));
    return Array.isArray(data) ? data : [];
  } catch {
    return [];
  }
}

function writeRegistry(connections) {
  const dir = getConnectionsDir();
  ensureDir(dir);
  writeFileSync(getRegistryPath(), JSON.stringify(connections, null, 2), 'utf8');
}

// ---------------------------------------------------------------------------
// Skill Detection
// ---------------------------------------------------------------------------

function getManagedSkillsDir() {
  const localAppData = process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local');
  return join(localAppData, 'cc-director', 'skills', 'managed');
}

function managedSkillExists(name) {
  const skillPath = join(getManagedSkillsDir(), `${name}.skill.md`);
  return existsSync(skillPath);
}

// ---------------------------------------------------------------------------
// Validation
// ---------------------------------------------------------------------------

const NAME_PATTERN = /^[a-z0-9-]+$/;

function validateName(name) {
  if (!name || typeof name !== 'string') {
    throw new Error('Connection name is required');
  }
  if (!NAME_PATTERN.test(name)) {
    throw new Error('Connection name must be lowercase alphanumeric with hyphens (e.g., "linkedin", "dev-studio")');
  }
  if (name.length > 50) {
    throw new Error('Connection name must be 50 characters or less');
  }
}

// ---------------------------------------------------------------------------
// CRUD
// ---------------------------------------------------------------------------

export function listConnections() {
  return readRegistry();
}

export function getConnection(name) {
  const connections = readRegistry();
  return connections.find(c => c.name === name) || null;
}

export function createConnection({ name, url, toolBinding, browser = 'chrome', description = '', skillName = null, ignoreCertErrors = false }) {
  validateName(name);

  const connections = readRegistry();
  if (connections.find(c => c.name === name)) {
    throw new Error(`Connection "${name}" already exists`);
  }

  // Auto-detect managed skill: explicit skillName, or fall back to connection name
  const resolvedSkillName = skillName || name;
  const skillType = managedSkillExists(resolvedSkillName) ? 'managed' : 'none';

  const connection = {
    name,
    description: description || '',
    url: url || null,
    toolBinding: toolBinding || null,
    browser,
    ignoreCertErrors: !!ignoreCertErrors,
    skillName: skillName || null,
    createdAt: new Date().toISOString(),
    status: 'disconnected',
    skill: {
      type: skillType,
      managedName: skillType === 'managed' ? resolvedSkillName : null,
    },
  };

  connections.push(connection);
  writeRegistry(connections);

  // Create profile directory
  ensureDir(getProfileDir(name));

  return connection;
}

export function updateConnection(name, updates) {
  const connections = readRegistry();
  const idx = connections.findIndex(c => c.name === name);
  if (idx === -1) {
    throw new Error(`Connection "${name}" not found`);
  }

  const allowed = ['url', 'toolBinding', 'browser', 'status', 'description', 'skill', 'skillName', 'ignoreCertErrors'];
  for (const key of Object.keys(updates)) {
    if (allowed.includes(key)) {
      connections[idx][key] = updates[key];
    }
  }

  writeRegistry(connections);
  return connections[idx];
}

export function deleteConnection(name) {
  const connections = readRegistry();
  const filtered = connections.filter(c => c.name !== name);
  if (filtered.length === connections.length) {
    throw new Error(`Connection "${name}" not found`);
  }
  writeRegistry(filtered);
  return true;
}

export function setConnectionStatus(name, status) {
  return updateConnection(name, { status });
}

export function findConnectionByTool(toolName) {
  const connections = readRegistry();
  return connections.find(c => c.toolBinding === toolName) || null;
}

// ---------------------------------------------------------------------------
// Profile Directory
// ---------------------------------------------------------------------------

export function getProfileDir(name) {
  return join(getConnectionsDir(), name);
}

// ---------------------------------------------------------------------------
// Exports for paths
// ---------------------------------------------------------------------------

export { getConnectionsDir, getRegistryPath };
