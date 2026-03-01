/**
 * Console formatter -- ASCII output matching cc-websiteaudit style.
 */

import { effortToHours } from '../weekly-planner.mjs';

const W = 60;

/**
 * Format and print the weekly plan to console.
 */
export function formatConsole(plan, audit) {
  const lines = [];

  // Header
  lines.push('');
  lines.push('cc-brandingrecommendations v1.0.0');
  lines.push('='.repeat(W));
  lines.push('  Branding Recommendations for: ' + audit.hostname);
  lines.push('  Audit Date:   ' + audit.date);
  lines.push('  Audit Score:  ' + audit.overall.grade + ' (' + audit.overall.score + '/100)');
  lines.push('  Budget:       ' + plan.budget + ' (' + plan.weeklyCapacity + ' hrs/week)');
  lines.push('  Total Recs:   ' + plan.totalRecs);
  lines.push('  Total Hours:  ' + plan.totalHours.toFixed(1) + 'h estimated');
  lines.push('='.repeat(W));
  lines.push('');

  // Phase summary
  lines.push('  Action Plan Summary:');
  lines.push('  ' + '-'.repeat(W - 4));

  for (const phase of plan.phases) {
    if (phase.recCount === 0) continue;
    const name = padRight(phase.name, 22);
    const weeks = padRight('Weeks ' + phase.weeks, 14);
    lines.push('  ' + name + weeks + phase.recCount + ' tasks (' + phase.totalHours.toFixed(1) + 'h)');
  }

  lines.push('');

  // Detailed phases
  for (const phase of plan.phases) {
    if (phase.recCount === 0) continue;

    lines.push('  ' + phase.name + ' (Weeks ' + phase.weeks + ')');
    lines.push('  ' + '-'.repeat(W - 4));

    for (let i = 0; i < phase.recs.length; i++) {
      const rec = phase.recs[i];
      const icon = priorityIcon(rec.priority);
      const hours = effortToHours(rec.effort).toFixed(1) + 'h';
      lines.push('  ' + (i + 1) + '. ' + icon + ' ' + rec.title + ' (' + hours + ')');
      lines.push('       Impact: ' + rec.impact + '/5  Effort: ' + rec.effort + '/5  Priority: ' + rec.priority);
      lines.push('       ' + truncate(rec.what, W - 8));

      if (rec.how && rec.how.length > 0) {
        lines.push('       Steps:');
        for (const step of rec.how) {
          lines.push('         - ' + truncate(step, W - 12));
        }
      }

      lines.push('');
    }
  }

  // Weekly schedule
  const weekNums = Object.keys(plan.weeks).map(Number).sort((a, b) => a - b);
  if (weekNums.length > 0) {
    lines.push('  Weekly Schedule:');
    lines.push('  ' + '-'.repeat(W - 4));

    for (const weekNum of weekNums) {
      const week = plan.weeks[weekNum];
      lines.push('  Week ' + weekNum + ' (' + week.totalHours.toFixed(1) + 'h):');
      for (const rec of week.recs) {
        const icon = priorityIcon(rec.priority);
        lines.push('    ' + icon + ' ' + rec.title);
      }
    }

    lines.push('');
  }

  // Footer
  lines.push('='.repeat(W));
  lines.push('  Use --format json for machine-readable output.');
  lines.push('  Use -o plan.md for markdown report.');
  lines.push('='.repeat(W));

  return lines.join('\n');
}

function priorityIcon(priority) {
  switch (priority) {
    case 'Quick Win': return '[+]';
    case 'Strategic': return '[*]';
    case 'Easy Fill': return '[~]';
    case 'Deprioritize': return '[-]';
    default: return '[?]';
  }
}

function padRight(str, len) {
  while (str.length < len) str += ' ';
  return str;
}

function truncate(str, maxLen) {
  if (str.length <= maxLen) return str;
  return str.slice(0, maxLen - 3) + '...';
}
