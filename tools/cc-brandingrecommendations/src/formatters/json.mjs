/**
 * JSON formatter -- outputs full weekly plan as JSON.
 */

/**
 * Format the weekly plan as a JSON string.
 */
export function formatJson(plan, audit) {
  const output = {
    tool: 'cc-brandingrecommendations',
    version: '1.0.0',
    generatedAt: new Date().toISOString(),
    auditSource: {
      url: audit.url,
      hostname: audit.hostname,
      date: audit.date,
      overallScore: audit.overall.score,
      overallGrade: audit.overall.grade,
    },
    plan: {
      budget: plan.budget,
      weeklyCapacity: plan.weeklyCapacity,
      totalRecommendations: plan.totalRecs,
      totalEstimatedHours: plan.totalHours,
    },
    phases: plan.phases.map(phase => ({
      name: phase.name,
      weeks: phase.weeks,
      recommendationCount: phase.recCount,
      totalHours: phase.totalHours,
      recommendations: phase.recs.map(formatRec),
    })),
    weeklySchedule: formatWeeks(plan.weeks),
    allRecommendations: getAllRecs(plan).map(formatRec),
  };

  return JSON.stringify(output, null, 2);
}

/**
 * Format a single recommendation for JSON output.
 */
function formatRec(rec) {
  return {
    id: rec.id,
    title: rec.title,
    category: rec.category,
    priority: rec.priority,
    impact: rec.impact,
    effort: rec.effort,
    what: rec.what,
    why: rec.why,
    how: rec.how,
    measures: rec.measures,
    citation: rec.citation,
    sourceCheck: rec.sourceCheck,
    sourceStatus: rec.sourceStatus,
  };
}

/**
 * Format weeks for JSON output.
 */
function formatWeeks(weeks) {
  const result = {};
  for (const [weekNum, weekData] of Object.entries(weeks)) {
    result['week-' + weekNum] = {
      totalHours: weekData.totalHours,
      tasks: weekData.recs.map(r => ({
        id: r.id,
        title: r.title,
        priority: r.priority,
        estimatedHours: r.effort,
      })),
    };
  }
  return result;
}

/**
 * Get all recommendations from the plan (phases + maintenance).
 */
function getAllRecs(plan) {
  const all = [];
  for (const phase of plan.phases) {
    all.push(...phase.recs);
  }
  return all;
}
