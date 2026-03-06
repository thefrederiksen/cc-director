// CC Browser v2 - Navigation Skills
// Manages skill files for browser connections: managed skills, custom forks, learned patterns.

import { readFileSync, writeFileSync, existsSync, mkdirSync, unlinkSync, readdirSync } from 'fs';
import { join, dirname } from 'path';
import { homedir } from 'os';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));

// ---------------------------------------------------------------------------
// Paths
// ---------------------------------------------------------------------------

function getLocalAppData() {
  return process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local');
}

function getManagedSkillsDir() {
  return join(getLocalAppData(), 'cc-director', 'skills', 'managed');
}

function getConnectionsDir() {
  return join(getLocalAppData(), 'cc-director', 'connections');
}

function getConnectionDir(connName) {
  return join(getConnectionsDir(), connName);
}

// Source managed skills (in repo or deployed alongside)
function getSourceSkillsDir() {
  // When deployed: _cc-browser/skills/
  // When running from repo: tools/cc-browser/skills/
  const deployed = join(__dirname, '..', 'skills');
  if (existsSync(deployed)) return deployed;
  return null;
}

// ---------------------------------------------------------------------------
// Managed Skills
// ---------------------------------------------------------------------------

export function listManagedSkills() {
  const dir = getManagedSkillsDir();
  if (!existsSync(dir)) return [];

  const manifestPath = join(dir, 'manifest.json');
  if (!existsSync(manifestPath)) return [];

  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  return Object.entries(manifest.skills || {}).map(([name, info]) => ({
    name,
    file: info.file,
    site: info.site,
    version: info.version,
  }));
}

export function getManagedSkill(name) {
  const dir = getManagedSkillsDir();
  const filePath = join(dir, `${name}.skill.md`);
  if (!existsSync(filePath)) return null;
  return readFileSync(filePath, 'utf8');
}

// ---------------------------------------------------------------------------
// Custom Skills (per-connection)
// ---------------------------------------------------------------------------

function getCustomSkillPath(connName) {
  return join(getConnectionDir(connName), 'skill.md');
}

function getCustomSkill(connName) {
  const path = getCustomSkillPath(connName);
  if (!existsSync(path)) return null;
  return readFileSync(path, 'utf8');
}

// ---------------------------------------------------------------------------
// Learned Patterns
// ---------------------------------------------------------------------------

function getLearnedPatternsPath(connName) {
  return join(getConnectionDir(connName), 'learned-patterns.md');
}

export function getLearnedPatterns(connName) {
  const path = getLearnedPatternsPath(connName);
  if (!existsSync(path)) return null;
  return readFileSync(path, 'utf8');
}

export function appendLearnedPattern(connName, pattern) {
  const dir = getConnectionDir(connName);
  if (!existsSync(dir)) {
    mkdirSync(dir, { recursive: true });
  }

  const path = getLearnedPatternsPath(connName);
  const date = new Date().toISOString().split('T')[0];
  const entry = `\n## ${date}: ${pattern.split('\n')[0]}\n${pattern}\n`;

  if (existsSync(path)) {
    const existing = readFileSync(path, 'utf8');
    writeFileSync(path, existing + entry, 'utf8');
  } else {
    writeFileSync(path, `# Learned Patterns\n${entry}`, 'utf8');
  }
}

export function clearLearnedPatterns(connName) {
  const path = getLearnedPatternsPath(connName);
  if (existsSync(path)) {
    unlinkSync(path);
    return true;
  }
  return false;
}

// ---------------------------------------------------------------------------
// Skill Resolution
// ---------------------------------------------------------------------------

export function resolveSkill(connName, skillNameOverride) {
  const skillName = skillNameOverride || connName;

  // 1. Custom skill (always keyed by connection name)
  const custom = getCustomSkill(connName);
  if (custom) {
    return {
      type: 'custom',
      content: custom,
      learnedPatterns: getLearnedPatterns(connName),
    };
  }

  // 2. Managed skill by skillName (allows "my-work-linkedin" -> "linkedin")
  const managed = getManagedSkill(skillName);
  if (managed) {
    return {
      type: 'managed',
      content: managed,
      learnedPatterns: getLearnedPatterns(connName),
    };
  }

  // 3. No skill
  return {
    type: 'none',
    content: null,
    learnedPatterns: getLearnedPatterns(connName),
  };
}

// ---------------------------------------------------------------------------
// Fork / Reset
// ---------------------------------------------------------------------------

export function forkSkill(connName, skillNameOverride) {
  const skillName = skillNameOverride || connName;
  const managed = getManagedSkill(skillName);
  if (!managed) {
    throw new Error(`No managed skill found for "${skillName}" to fork`);
  }

  const dir = getConnectionDir(connName);
  if (!existsSync(dir)) {
    mkdirSync(dir, { recursive: true });
  }

  const customPath = getCustomSkillPath(connName);
  if (existsSync(customPath)) {
    throw new Error(`Custom skill already exists for "${connName}". Delete it first or edit directly.`);
  }

  // Parse frontmatter to update flavor and forked_from
  const forked = managed
    .replace(/flavor:\s*managed/, 'flavor: custom')
    .replace(/forked_from:\s*null/, `forked_from: "${connName}@${getSkillVersion(connName)}"`);

  writeFileSync(customPath, forked, 'utf8');
  return customPath;
}

export function resetSkill(connName) {
  const customPath = getCustomSkillPath(connName);
  if (!existsSync(customPath)) {
    throw new Error(`No custom skill to reset for "${connName}"`);
  }
  unlinkSync(customPath);
  return true;
}

// ---------------------------------------------------------------------------
// Listing (combined view)
// ---------------------------------------------------------------------------

export function listAllSkills() {
  const managed = listManagedSkills();
  const connectionsDir = getConnectionsDir();
  const custom = [];

  if (existsSync(connectionsDir)) {
    for (const entry of readdirSync(connectionsDir, { withFileTypes: true })) {
      if (entry.isDirectory()) {
        const skillPath = join(connectionsDir, entry.name, 'skill.md');
        if (existsSync(skillPath)) {
          custom.push({ name: entry.name, type: 'custom' });
        }
      }
    }
  }

  return { managed, custom };
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function getSkillVersion(name) {
  const dir = getManagedSkillsDir();
  const manifestPath = join(dir, 'manifest.json');
  if (!existsSync(manifestPath)) return 'unknown';

  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  return manifest.skills?.[name]?.version || 'unknown';
}

export { getManagedSkillsDir, getCustomSkillPath, getSourceSkillsDir };
