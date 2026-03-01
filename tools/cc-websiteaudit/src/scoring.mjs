/**
 * Scoring engine.
 * Converts check results into category scores and overall grade.
 */

const WEIGHTS = {
  'technical-seo': 0.20,
  'on-page-seo': 0.20,
  'security': 0.10,
  'structured-data': 0.10,
  'ai-readiness': 0.20,
  'performance': 0.15,
  'ai-visibility': 0.05,
};

const GRADE_TABLE = [
  { min: 97, grade: 'A+' },
  { min: 93, grade: 'A' },
  { min: 90, grade: 'A-' },
  { min: 87, grade: 'B+' },
  { min: 83, grade: 'B' },
  { min: 80, grade: 'B-' },
  { min: 77, grade: 'C+' },
  { min: 73, grade: 'C' },
  { min: 70, grade: 'C-' },
  { min: 67, grade: 'D+' },
  { min: 63, grade: 'D' },
  { min: 60, grade: 'D-' },
  { min: 0, grade: 'F' },
];

/**
 * Calculate scores for each category.
 * Returns { 'category-id': { name, score, grade, weight, checks } }
 */
export function calculateScores(results) {
  const scores = {};

  for (const [categoryId, category] of Object.entries(results)) {
    const checks = category.checks || [];
    const scorable = checks.filter(c => c.status !== 'SKIP');

    let score = 0;
    if (scorable.length > 0) {
      let total = 0;
      for (const check of scorable) {
        if (check.status === 'PASS') total += 100;
        else if (check.status === 'WARN') total += 50;
        // FAIL = 0
      }
      score = Math.round(total / scorable.length);
    }

    const weight = WEIGHTS[categoryId] || 0.10;
    const grade = scoreToGrade(score);

    scores[categoryId] = {
      name: category.name,
      score,
      grade,
      weight,
      checks,
    };
  }

  return scores;
}

/**
 * Calculate the overall weighted grade.
 */
export function calculateOverallGrade(scores) {
  let totalWeight = 0;
  let weightedScore = 0;

  for (const category of Object.values(scores)) {
    weightedScore += category.score * category.weight;
    totalWeight += category.weight;
  }

  // Normalize if total weight doesn't sum to 1.0 (e.g., missing categories)
  const score = totalWeight > 0 ? Math.round(weightedScore / totalWeight) : 0;
  const grade = scoreToGrade(score);

  return { score, grade };
}

/**
 * Convert a numeric score (0-100) to a letter grade.
 */
export function scoreToGrade(score) {
  for (const { min, grade } of GRADE_TABLE) {
    if (score >= min) return grade;
  }
  return 'F';
}

/**
 * Get the top quick wins across all categories.
 * Quick wins = high impact, low effort, status is FAIL or WARN.
 */
export function getQuickWins(scores, count = 5) {
  const findings = [];

  for (const category of Object.values(scores)) {
    for (const check of category.checks) {
      if (check.status === 'FAIL' || check.status === 'WARN') {
        findings.push({
          ...check,
          category: category.name,
          priority: (check.impact || 3) / (check.effort || 2),
        });
      }
    }
  }

  // Sort by priority (highest first), then by impact (highest first)
  findings.sort((a, b) => {
    if (b.priority !== a.priority) return b.priority - a.priority;
    return (b.impact || 0) - (a.impact || 0);
  });

  return findings.slice(0, count);
}
