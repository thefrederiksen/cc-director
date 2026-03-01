/**
 * Base generator utilities shared by all category generators.
 */

import { getSeverity } from '../data/benchmarks.mjs';
import { getTemplate } from '../data/templates.mjs';
import { getCitation, getCitationFinding } from '../data/citations.mjs';

/**
 * Generate recommendations for a single category.
 *
 * @param {string} categoryId - e.g. 'technical-seo'
 * @param {object} audit - The parsed audit object
 * @param {string} hostname - The site hostname for template interpolation
 * @returns {Array} Array of recommendation objects
 */
export function generateCategoryRecs(categoryId, audit, hostname) {
  const category = audit.categories[categoryId];
  if (!category) return [];

  const severity = getSeverity(category.score);
  const recs = [];

  for (const check of category.checks) {
    // Determine which severity template to use
    let templateSeverity = severity;

    // Override: FAIL checks always get at least 'improvement' severity template
    if (check.status === 'FAIL' && (severity === 'optimization' || severity === 'maintenance')) {
      templateSeverity = 'improvement';
    }
    // WARN checks in maintenance mode get 'optimization' template
    if (check.status === 'WARN' && severity === 'maintenance') {
      templateSeverity = 'optimization';
    }
    // PASS checks only get maintenance recs
    if (check.status === 'PASS') {
      templateSeverity = 'maintenance';
    }
    // SKIP checks produce no recommendations
    if (check.status === 'SKIP') continue;

    const template = getTemplate(check.id, templateSeverity);
    if (!template) continue;

    const citation = getCitationFinding(check.id);
    const citationShort = getCitation(check.id);

    recs.push({
      id: categoryId + '-' + check.id,
      title: interpolate(template.title, hostname, check.detail, citation),
      category: categoryId,
      priority: null, // Will be set by scoring.mjs
      impact: check.impact,
      effort: check.effort,
      what: interpolate(template.what, hostname, check.detail, citation),
      why: interpolate(template.why, hostname, check.detail, citation),
      how: template.how.map(step => interpolate(step, hostname, check.detail, citation)),
      measures: template.measures.map(m => interpolate(m, hostname, check.detail, citation)),
      citation: citationShort,
      sourceCheck: check.id,
      sourceStatus: check.status,
    });
  }

  return recs;
}

/**
 * Interpolate template placeholders.
 */
function interpolate(text, hostname, detail, citation) {
  return text
    .replace(/\{hostname\}/g, hostname)
    .replace(/\{detail\}/g, detail)
    .replace(/\{citation\}/g, citation);
}
