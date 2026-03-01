/**
 * Audit parser -- reads and validates cc-websiteaudit JSON output.
 */

import { readFileSync } from 'fs';

const VALID_CATEGORIES = [
  'technical-seo',
  'on-page-seo',
  'security',
  'structured-data',
  'ai-readiness',
];

const VALID_STATUSES = ['PASS', 'WARN', 'FAIL', 'SKIP'];

/**
 * Parse an audit JSON file from cc-websiteaudit.
 * Returns the validated audit object.
 * Throws on file read errors or invalid structure.
 */
export function parseAuditFile(filePath) {
  let raw;
  try {
    raw = readFileSync(filePath, 'utf8');
  } catch (err) {
    throw new Error('Cannot read audit file: ' + filePath + ' (' + err.message + ')');
  }

  let data;
  try {
    data = JSON.parse(raw);
  } catch (err) {
    throw new Error('Invalid JSON in audit file: ' + err.message);
  }

  return validateAudit(data);
}

/**
 * Validate the audit data structure.
 * Returns a normalized audit object.
 */
export function validateAudit(data) {
  if (!data || typeof data !== 'object') {
    throw new Error('Audit data must be an object');
  }

  if (!data.url || typeof data.url !== 'string') {
    throw new Error('Audit data must have a "url" string property');
  }

  if (!data.hostname || typeof data.hostname !== 'string') {
    throw new Error('Audit data must have a "hostname" string property');
  }

  if (!data.categories || typeof data.categories !== 'object') {
    throw new Error('Audit data must have a "categories" object');
  }

  // Validate each category present
  const categories = {};
  for (const [id, cat] of Object.entries(data.categories)) {
    if (!VALID_CATEGORIES.includes(id)) {
      // Skip unknown categories with a warning, do not throw
      continue;
    }

    if (!cat.checks || !Array.isArray(cat.checks)) {
      throw new Error('Category "' + id + '" must have a "checks" array');
    }

    // Validate and normalize each check
    const checks = cat.checks.map(check => {
      if (!check.id || typeof check.id !== 'string') {
        throw new Error('Each check must have a string "id" in category "' + id + '"');
      }
      if (!check.status || !VALID_STATUSES.includes(check.status)) {
        throw new Error('Check "' + check.id + '" has invalid status: ' + check.status);
      }

      return {
        id: check.id,
        name: check.name || check.id,
        status: check.status,
        detail: check.detail || '',
        impact: typeof check.impact === 'number' ? check.impact : 3,
        effort: typeof check.effort === 'number' ? check.effort : 2,
      };
    });

    categories[id] = {
      name: cat.name || id,
      score: typeof cat.score === 'number' ? cat.score : 0,
      grade: cat.grade || 'F',
      weight: typeof cat.weight === 'number' ? cat.weight : 0.10,
      checks,
    };
  }

  return {
    url: data.url,
    hostname: data.hostname,
    date: data.date || new Date().toISOString().split('T')[0],
    pagesCrawled: data.pagesCrawled || 0,
    overall: data.overall || { score: 0, grade: 'F' },
    categories,
    quickWins: data.quickWins || [],
  };
}

/**
 * Get list of category IDs present in the audit.
 */
export function getAuditCategories(audit) {
  return Object.keys(audit.categories);
}

/**
 * Get all checks with FAIL or WARN status across all categories.
 */
export function getFailingChecks(audit) {
  const failing = [];
  for (const [catId, cat] of Object.entries(audit.categories)) {
    for (const check of cat.checks) {
      if (check.status === 'FAIL' || check.status === 'WARN') {
        failing.push({ ...check, category: catId });
      }
    }
  }
  return failing;
}
