/**
 * Weekly planner -- distributes recommendations across weekly buckets
 * based on priority phase and budget capacity.
 */

import { EFFORT_TO_HOURS, BUDGET_CAPACITY, PHASES } from './data/benchmarks.mjs';

/**
 * Create a weekly plan from sorted recommendations.
 *
 * @param {Array} recs - Sorted recommendation array (from scoring.mjs)
 * @param {string} budget - 'low', 'medium', or 'high'
 * @returns {object} Weekly plan object
 */
export function createWeeklyPlan(recs, budget) {
  const weeklyCapacity = BUDGET_CAPACITY[budget] || BUDGET_CAPACITY.medium;

  // Group recs by priority bucket
  const buckets = {
    'Quick Win': [],
    'Easy Fill': [],
    'Strategic': [],
    'Deprioritize': [],
  };

  const maintenance = [];

  for (const rec of recs) {
    if (rec.sourceStatus === 'PASS') {
      maintenance.push(rec);
    } else if (buckets[rec.priority]) {
      buckets[rec.priority].push(rec);
    } else {
      buckets['Deprioritize'].push(rec);
    }
  }

  const weeks = {};
  let currentWeek = 1;

  // Phase 1: Quick Wins (Weeks 1-2)
  currentWeek = assignToWeeks(weeks, buckets['Quick Win'], 1, 2, weeklyCapacity, currentWeek);

  // Phase 2: Foundation (Weeks 3-4) -- Easy Fills + critical Strategic items
  const criticalStrategic = buckets['Strategic'].filter(r => r.impact >= 5);
  const remainingStrategic = buckets['Strategic'].filter(r => r.impact < 5);
  const foundationItems = [...buckets['Easy Fill'], ...criticalStrategic];
  currentWeek = assignToWeeks(weeks, foundationItems, 3, 4, weeklyCapacity, Math.max(currentWeek, 3));

  // Phase 3: Strategic (Weeks 5-8)
  currentWeek = assignToWeeks(weeks, remainingStrategic, 5, 8, weeklyCapacity, Math.max(currentWeek, 5));

  // Phase 4: Optimization (Weeks 9-12)
  currentWeek = assignToWeeks(weeks, buckets['Deprioritize'], 9, 12, weeklyCapacity, Math.max(currentWeek, 9));

  return {
    budget,
    weeklyCapacity,
    totalRecs: recs.length,
    totalHours: recs.reduce((sum, r) => sum + effortToHours(r.effort), 0),
    weeks,
    maintenance,
    phases: summarizePhases(weeks, maintenance),
  };
}

/**
 * Assign recommendations to weekly buckets within a week range.
 * Returns the next available week.
 */
function assignToWeeks(weeks, recs, startWeek, endWeek, weeklyCapacity, currentWeek) {
  let week = Math.max(startWeek, currentWeek);
  let weekHours = getWeekHours(weeks, week);

  for (const rec of recs) {
    const hours = effortToHours(rec.effort);

    // If this rec would exceed capacity and we have room to advance
    if (weekHours + hours > weeklyCapacity && weekHours > 0 && week < endWeek) {
      week++;
      weekHours = getWeekHours(weeks, week);
    }

    if (!weeks[week]) {
      weeks[week] = { recs: [], totalHours: 0 };
    }
    weeks[week].recs.push(rec);
    weeks[week].totalHours += hours;
    weekHours += hours;

    // If we've exceeded capacity after adding, move to next week for the next item
    if (weekHours >= weeklyCapacity && week < endWeek) {
      week++;
      weekHours = getWeekHours(weeks, week);
    }
  }

  return week + 1;
}

/**
 * Get current hours used in a week.
 */
function getWeekHours(weeks, week) {
  return weeks[week] ? weeks[week].totalHours : 0;
}

/**
 * Convert effort score to hours.
 */
export function effortToHours(effort) {
  return EFFORT_TO_HOURS[effort] || EFFORT_TO_HOURS[3];
}

/**
 * Summarize phases from the weekly plan.
 */
function summarizePhases(weeks, maintenance) {
  const phases = [];

  for (const phase of PHASES) {
    const phaseRecs = [];
    let phaseHours = 0;

    for (const weekNum of phase.weeks) {
      if (weeks[weekNum]) {
        phaseRecs.push(...weeks[weekNum].recs);
        phaseHours += weeks[weekNum].totalHours;
      }
    }

    phases.push({
      name: phase.name,
      weeks: phase.weeks[0] + '-' + phase.weeks[phase.weeks.length - 1],
      recCount: phaseRecs.length,
      totalHours: phaseHours,
      recs: phaseRecs,
    });
  }

  // Ongoing maintenance phase
  phases.push({
    name: 'Ongoing Maintenance',
    weeks: 'Ongoing',
    recCount: maintenance.length,
    totalHours: maintenance.reduce((sum, r) => sum + effortToHours(r.effort), 0),
    recs: maintenance,
  });

  return phases;
}
