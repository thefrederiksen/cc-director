/**
 * Benchmarks, thresholds, capacity tables, and effort-to-hours mapping.
 */

// Severity thresholds based on category score
export const SEVERITY_THRESHOLDS = {
  critical: 60,      // score < 60
  improvement: 80,   // score 60-79
  optimization: 90,  // score 80-89
  maintenance: 100,  // score 90+
};

/**
 * Determine severity level from a category score.
 */
export function getSeverity(score) {
  if (score < SEVERITY_THRESHOLDS.critical) return 'critical';
  if (score < SEVERITY_THRESHOLDS.improvement) return 'improvement';
  if (score < SEVERITY_THRESHOLDS.optimization) return 'optimization';
  return 'maintenance';
}

// Effort units (1-5) mapped to estimated hours
export const EFFORT_TO_HOURS = {
  1: 1,
  2: 2.5,
  3: 5,
  4: 10,
  5: 18,
};

// Weekly hour capacity by budget level
export const BUDGET_CAPACITY = {
  low: 5,
  medium: 10,
  high: 20,
};

// Phase definitions with week ranges
export const PHASES = [
  { name: 'Quick Wins', weeks: [1, 2], priority: 'Quick Win' },
  { name: 'Foundation', weeks: [3, 4], priority: 'Easy Fill' },
  { name: 'Strategic', weeks: [5, 6, 7, 8], priority: 'Strategic' },
  { name: 'Optimization', weeks: [9, 10, 11, 12], priority: 'Deprioritize' },
];

// Category weights (matching cc-websiteaudit scoring)
export const CATEGORY_WEIGHTS = {
  'technical-seo': 0.20,
  'on-page-seo': 0.20,
  'security': 0.10,
  'structured-data': 0.10,
  'ai-readiness': 0.20,
};

// All known check IDs with their default impact/effort values
export const CHECK_DEFAULTS = {
  // technical-seo
  'robots-txt': { impact: 4, effort: 1 },
  'xml-sitemap': { impact: 4, effort: 2 },
  'canonicals': { impact: 3, effort: 2 },
  'https': { impact: 5, effort: 2 },
  'redirect-chains': { impact: 3, effort: 2 },
  'status-codes': { impact: 4, effort: 2 },
  'crawl-depth': { impact: 3, effort: 3 },
  'url-structure': { impact: 2, effort: 3 },
  // on-page-seo
  'title-tags': { impact: 4, effort: 1 },
  'meta-descriptions': { impact: 3, effort: 1 },
  'heading-hierarchy': { impact: 3, effort: 2 },
  'image-alt-text': { impact: 3, effort: 1 },
  'internal-linking': { impact: 3, effort: 2 },
  'content-length': { impact: 3, effort: 3 },
  'duplicate-content': { impact: 3, effort: 3 },
  'open-graph': { impact: 2, effort: 1 },
  // security
  'hsts': { impact: 4, effort: 1 },
  'csp': { impact: 4, effort: 3 },
  'x-content-type-options': { impact: 3, effort: 1 },
  'x-frame-options': { impact: 3, effort: 1 },
  'referrer-policy': { impact: 2, effort: 1 },
  'permissions-policy': { impact: 2, effort: 1 },
  // structured-data
  'json-ld-present': { impact: 5, effort: 2 },
  'organization-schema': { impact: 4, effort: 2 },
  'article-schema': { impact: 3, effort: 2 },
  'faq-schema': { impact: 5, effort: 1 },
  'breadcrumb-schema': { impact: 2, effort: 1 },
  'schema-validity': { impact: 3, effort: 2 },
  // ai-readiness
  'llms-txt': { impact: 4, effort: 1 },
  'ai-crawler-access': { impact: 5, effort: 1 },
  'content-citability': { impact: 4, effort: 3 },
  'passage-structure': { impact: 3, effort: 3 },
  'semantic-html': { impact: 3, effort: 2 },
  'entity-clarity': { impact: 4, effort: 2 },
  'question-coverage': { impact: 4, effort: 2 },
};
