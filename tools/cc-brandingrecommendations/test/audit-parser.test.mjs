import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { parseAuditFile, validateAudit, getAuditCategories, getFailingChecks } from '../src/audit-parser.mjs';
import { fileURLToPath } from 'url';
import { dirname, join } from 'path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const fixturePath = join(__dirname, 'fixtures', 'sample-audit.json');

describe('parseAuditFile', () => {
  it('should parse a valid audit JSON file', () => {
    const audit = parseAuditFile(fixturePath);
    assert.equal(audit.url, 'https://example.com');
    assert.equal(audit.hostname, 'example.com');
    assert.equal(audit.overall.score, 62);
    assert.ok(audit.categories['technical-seo']);
    assert.ok(audit.categories['on-page-seo']);
    assert.ok(audit.categories['security']);
    assert.ok(audit.categories['structured-data']);
    assert.ok(audit.categories['ai-readiness']);
  });

  it('should throw on non-existent file', () => {
    assert.throws(() => parseAuditFile('/nonexistent/file.json'), /Cannot read audit file/);
  });
});

describe('validateAudit', () => {
  it('should validate a correct audit object', () => {
    const data = {
      url: 'https://test.com',
      hostname: 'test.com',
      categories: {
        'technical-seo': {
          name: 'Technical SEO',
          score: 80,
          grade: 'B-',
          weight: 0.20,
          checks: [
            { id: 'robots-txt', name: 'robots.txt', status: 'PASS', detail: 'OK', impact: 4, effort: 1 },
          ],
        },
      },
    };
    const result = validateAudit(data);
    assert.equal(result.url, 'https://test.com');
    assert.equal(result.categories['technical-seo'].checks.length, 1);
  });

  it('should throw on missing url', () => {
    assert.throws(() => validateAudit({ hostname: 'x', categories: {} }), /must have a "url"/);
  });

  it('should throw on missing hostname', () => {
    assert.throws(() => validateAudit({ url: 'x', categories: {} }), /must have a "hostname"/);
  });

  it('should throw on missing categories', () => {
    assert.throws(() => validateAudit({ url: 'x', hostname: 'x' }), /must have a "categories"/);
  });

  it('should throw on invalid check status', () => {
    const data = {
      url: 'https://test.com',
      hostname: 'test.com',
      categories: {
        'technical-seo': {
          checks: [{ id: 'robots-txt', status: 'INVALID' }],
        },
      },
    };
    assert.throws(() => validateAudit(data), /invalid status/);
  });

  it('should skip unknown categories without throwing', () => {
    const data = {
      url: 'https://test.com',
      hostname: 'test.com',
      categories: {
        'unknown-cat': { checks: [] },
        'technical-seo': {
          checks: [{ id: 'robots-txt', status: 'PASS' }],
        },
      },
    };
    const result = validateAudit(data);
    assert.ok(result.categories['technical-seo']);
    assert.equal(result.categories['unknown-cat'], undefined);
  });

  it('should use defaults for missing optional fields', () => {
    const data = {
      url: 'https://test.com',
      hostname: 'test.com',
      categories: {
        'security': {
          checks: [{ id: 'hsts', status: 'FAIL' }],
        },
      },
    };
    const result = validateAudit(data);
    assert.equal(result.date.length, 10); // YYYY-MM-DD
    assert.equal(result.pagesCrawled, 0);
    assert.equal(result.categories['security'].checks[0].impact, 3); // default
    assert.equal(result.categories['security'].checks[0].effort, 2); // default
    assert.equal(result.categories['security'].checks[0].name, 'hsts'); // falls back to id
  });
});

describe('getAuditCategories', () => {
  it('should return category IDs from audit', () => {
    const audit = parseAuditFile(fixturePath);
    const cats = getAuditCategories(audit);
    assert.equal(cats.length, 5);
    assert.ok(cats.includes('technical-seo'));
    assert.ok(cats.includes('ai-readiness'));
  });
});

describe('getFailingChecks', () => {
  it('should return all FAIL and WARN checks', () => {
    const audit = parseAuditFile(fixturePath);
    const failing = getFailingChecks(audit);
    assert.ok(failing.length > 0);
    for (const check of failing) {
      assert.ok(check.status === 'FAIL' || check.status === 'WARN');
      assert.ok(check.category);
    }
  });

  it('should include category ID on each check', () => {
    const audit = parseAuditFile(fixturePath);
    const failing = getFailingChecks(audit);
    const categories = new Set(failing.map(c => c.category));
    assert.ok(categories.size > 1); // failing checks from multiple categories
  });
});
