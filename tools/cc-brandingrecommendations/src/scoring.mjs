/**
 * Priority classification and sorting for recommendations.
 *
 * Uses the Eisenhower matrix approach:
 *   - Quick Win:    high impact (>=4), low effort (<=2)
 *   - Strategic:    high impact (>=4), high effort (>=3)
 *   - Easy Fill:    low impact (<=3), low effort (<=2)
 *   - Deprioritize: low impact (<=3), high effort (>=3)
 */

/**
 * Classify a recommendation into a priority bucket.
 */
export function classifyPriority(impact, effort) {
  const highImpact = impact >= 4;
  const lowEffort = effort <= 2;

  if (highImpact && lowEffort) return 'Quick Win';
  if (highImpact && !lowEffort) return 'Strategic';
  if (!highImpact && lowEffort) return 'Easy Fill';
  return 'Deprioritize';
}

// Priority sort order (lower = higher priority)
const PRIORITY_ORDER = {
  'Quick Win': 1,
  'Strategic': 2,
  'Easy Fill': 3,
  'Deprioritize': 4,
};

/**
 * Get a numeric priority score for sorting.
 * Lower is higher priority.
 * Combines priority bucket with impact/effort ratio for fine-grained ordering.
 */
export function priorityScore(rec) {
  const bucketScore = PRIORITY_ORDER[rec.priority] || 4;
  const ratio = (rec.impact || 3) / (rec.effort || 2);
  // Primary sort by bucket (multiply by 100 to dominate), secondary by ratio (inverted)
  return bucketScore * 100 - ratio * 10;
}

/**
 * Sort recommendations by priority.
 * Quick Wins first, then Strategic, Easy Fill, Deprioritize.
 * Within each bucket, sort by impact/effort ratio (highest first).
 */
export function sortRecommendations(recs) {
  return [...recs].sort((a, b) => {
    const scoreA = priorityScore(a);
    const scoreB = priorityScore(b);
    if (scoreA !== scoreB) return scoreA - scoreB;
    // Tie-break by impact descending
    return (b.impact || 0) - (a.impact || 0);
  });
}

/**
 * Classify and sort a list of recommendations.
 * Mutates the priority field on each recommendation, then returns sorted.
 */
export function classifyAndSort(recs) {
  for (const rec of recs) {
    rec.priority = classifyPriority(rec.impact, rec.effort);
  }
  return sortRecommendations(recs);
}
