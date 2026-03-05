// CC Browser v2 - Navigation Skills Tests
// Tests skill resolution, fork/reset, and learned patterns.

import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, rmSync, existsSync, readFileSync, writeFileSync, cpSync } from 'fs';
import { join } from 'path';
import { tmpdir } from 'os';
import { dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));

// Override LOCALAPPDATA to use temp directory for testing
const testDir = join(tmpdir(), `cc-browser-skills-test-${Date.now()}`);
process.env.LOCALAPPDATA = testDir;

// Set up managed skills directory with test data
const managedDir = join(testDir, 'cc-director', 'skills', 'managed');
const connectionsDir = join(testDir, 'cc-director', 'connections');
mkdirSync(managedDir, { recursive: true });
mkdirSync(connectionsDir, { recursive: true });

// Copy real skill files from repo
const repoSkillsDir = join(__dirname, '..', '..', 'skills');
cpSync(join(repoSkillsDir, 'manifest.json'), join(managedDir, 'manifest.json'));
cpSync(join(repoSkillsDir, 'linkedin.skill.md'), join(managedDir, 'linkedin.skill.md'));
cpSync(join(repoSkillsDir, 'reddit.skill.md'), join(managedDir, 'reddit.skill.md'));
cpSync(join(repoSkillsDir, 'spotify.skill.md'), join(managedDir, 'spotify.skill.md'));

// Import after env override
const {
  listManagedSkills, getManagedSkill, resolveSkill,
  forkSkill, resetSkill, listAllSkills,
  appendLearnedPattern, getLearnedPatterns, clearLearnedPatterns,
} = await import('../../src/skills.mjs');

describe('Navigation Skills', () => {
  afterEach(() => {
    // Clean up connection-level custom skills and learned patterns
    try {
      rmSync(join(connectionsDir, 'linkedin'), { recursive: true, force: true });
      rmSync(join(connectionsDir, 'test-conn'), { recursive: true, force: true });
    } catch {
      // Best effort
    }
  });

  describe('listManagedSkills', () => {
    it('should list all managed skills from manifest', () => {
      const skills = listManagedSkills();
      assert.ok(Array.isArray(skills));
      assert.equal(skills.length, 3);

      const names = skills.map(s => s.name);
      assert.ok(names.includes('linkedin'));
      assert.ok(names.includes('reddit'));
      assert.ok(names.includes('spotify'));
    });

    it('should include site and version for each skill', () => {
      const skills = listManagedSkills();
      const linkedin = skills.find(s => s.name === 'linkedin');
      assert.equal(linkedin.site, 'linkedin.com');
      assert.equal(linkedin.version, '2026.03.05');
    });
  });

  describe('getManagedSkill', () => {
    it('should return content for existing managed skill', () => {
      const content = getManagedSkill('linkedin');
      assert.ok(content);
      assert.ok(content.includes('LinkedIn Navigation Skill'));
      assert.ok(content.includes('name: linkedin'));
    });

    it('should return null for non-existent skill', () => {
      const content = getManagedSkill('nonexistent');
      assert.equal(content, null);
    });
  });

  describe('resolveSkill', () => {
    it('should resolve managed skill by connection name', () => {
      const skill = resolveSkill('linkedin');
      assert.equal(skill.type, 'managed');
      assert.ok(skill.content.includes('LinkedIn'));
      assert.equal(skill.learnedPatterns, null);
    });

    it('should return type none for unknown connection', () => {
      const skill = resolveSkill('unknown-site');
      assert.equal(skill.type, 'none');
      assert.equal(skill.content, null);
    });

    it('should prefer custom skill over managed', () => {
      const connDir = join(connectionsDir, 'linkedin');
      mkdirSync(connDir, { recursive: true });
      writeFileSync(join(connDir, 'skill.md'), '# Custom LinkedIn Skill\nMy custom version.');

      const skill = resolveSkill('linkedin');
      assert.equal(skill.type, 'custom');
      assert.ok(skill.content.includes('Custom LinkedIn Skill'));
    });

    it('should include learned patterns when available', () => {
      const connDir = join(connectionsDir, 'linkedin');
      mkdirSync(connDir, { recursive: true });
      writeFileSync(join(connDir, 'learned-patterns.md'), '# Learned\n## 2026-03-05: test');

      const skill = resolveSkill('linkedin');
      assert.ok(skill.learnedPatterns);
      assert.ok(skill.learnedPatterns.includes('test'));
    });
  });

  describe('forkSkill', () => {
    it('should create custom skill from managed', () => {
      mkdirSync(join(connectionsDir, 'linkedin'), { recursive: true });
      const path = forkSkill('linkedin');
      assert.ok(existsSync(path));

      const content = readFileSync(path, 'utf8');
      assert.ok(content.includes('flavor: custom'));
      assert.ok(content.includes('forked_from:'));
    });

    it('should throw if no managed skill exists', () => {
      assert.throws(() => forkSkill('no-such-skill'), /No managed skill/);
    });

    it('should throw if custom skill already exists', () => {
      const connDir = join(connectionsDir, 'reddit');
      mkdirSync(connDir, { recursive: true });
      writeFileSync(join(connDir, 'skill.md'), '# existing');
      assert.throws(() => forkSkill('reddit'), /already exists/);
      // Cleanup
      rmSync(connDir, { recursive: true, force: true });
    });
  });

  describe('resetSkill', () => {
    it('should remove custom skill', () => {
      const connDir = join(connectionsDir, 'linkedin');
      mkdirSync(connDir, { recursive: true });
      forkSkill('linkedin');

      resetSkill('linkedin');
      assert.ok(!existsSync(join(connDir, 'skill.md')));
    });

    it('should throw if no custom skill exists', () => {
      assert.throws(() => resetSkill('linkedin'), /No custom skill/);
    });

    it('should preserve learned patterns after reset', () => {
      const connDir = join(connectionsDir, 'linkedin');
      mkdirSync(connDir, { recursive: true });
      writeFileSync(join(connDir, 'learned-patterns.md'), '# Patterns\nKeep me');
      forkSkill('linkedin');

      resetSkill('linkedin');

      assert.ok(!existsSync(join(connDir, 'skill.md')));
      assert.ok(existsSync(join(connDir, 'learned-patterns.md')));
      const patterns = readFileSync(join(connDir, 'learned-patterns.md'), 'utf8');
      assert.ok(patterns.includes('Keep me'));
    });
  });

  describe('Learned Patterns', () => {
    it('should append a learned pattern', () => {
      appendLearnedPattern('test-conn', 'Profile headline selector changed to div.new-class');
      const patterns = getLearnedPatterns('test-conn');
      assert.ok(patterns);
      assert.ok(patterns.includes('Profile headline selector changed'));
      assert.ok(patterns.includes('div.new-class'));
    });

    it('should append multiple patterns', () => {
      appendLearnedPattern('test-conn', 'First pattern');
      appendLearnedPattern('test-conn', 'Second pattern');
      const patterns = getLearnedPatterns('test-conn');
      assert.ok(patterns.includes('First pattern'));
      assert.ok(patterns.includes('Second pattern'));
    });

    it('should return null when no patterns exist', () => {
      const patterns = getLearnedPatterns('no-patterns');
      assert.equal(patterns, null);
    });

    it('should clear learned patterns', () => {
      appendLearnedPattern('test-conn', 'Something');
      assert.ok(getLearnedPatterns('test-conn'));

      const cleared = clearLearnedPatterns('test-conn');
      assert.equal(cleared, true);
      assert.equal(getLearnedPatterns('test-conn'), null);
    });

    it('should return false when clearing non-existent patterns', () => {
      const cleared = clearLearnedPatterns('no-such-conn');
      assert.equal(cleared, false);
    });
  });

  describe('listAllSkills', () => {
    it('should list managed and custom skills', () => {
      const connDir = join(connectionsDir, 'linkedin');
      mkdirSync(connDir, { recursive: true });
      writeFileSync(join(connDir, 'skill.md'), '# Custom');

      const result = listAllSkills();
      assert.ok(result.managed.length >= 3);
      assert.ok(result.custom.length >= 1);
      assert.ok(result.custom.find(c => c.name === 'linkedin'));
    });
  });
});

// Cleanup
process.on('exit', () => {
  try {
    rmSync(testDir, { recursive: true, force: true });
  } catch {
    // Best effort
  }
});
