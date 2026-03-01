import { getQuickWins } from './scoring.mjs';

/**
 * Console reporter -- ASCII output with grades and quick wins.
 */
export function reportConsole(report, quietMode = false) {
  if (quietMode) {
    console.log(report.overall.grade + ' (' + report.overall.score + '/100)');
    return;
  }

  const w = 60;

  console.log('='.repeat(w));
  console.log('  cc-websiteaudit Report: ' + report.hostname);
  console.log('  Date: ' + report.date);
  console.log('  Pages Crawled: ' + report.pagesCrawled);
  console.log('='.repeat(w));
  console.log('');

  // Site Profile
  if (report.siteProfile) {
    const sp = report.siteProfile;
    console.log('  Site Profile:');
    console.log('  ' + '-'.repeat(w - 4));
    console.log('  Hosting:    ' + sp.hosting.join(', '));
    console.log('  Framework:  ' + sp.framework.join(', '));
    if (sp.server) {
      console.log('  Server:     ' + sp.server);
    }
    if (sp.additionalTech.length > 0) {
      console.log('  Other Tech: ' + sp.additionalTech.join(', '));
    }
    console.log('');
  }

  console.log('  OVERALL GRADE: ' + report.overall.grade + '  (' + report.overall.score + '/100)');
  console.log('');
  console.log('  Category Grades:');
  console.log('  ' + '-'.repeat(w - 4));

  // Sort categories by weight descending
  const sorted = Object.entries(report.categories)
    .sort((a, b) => b[1].weight - a[1].weight);

  for (const [id, cat] of sorted) {
    const name = padRight(cat.name, 24);
    const grade = padRight(cat.grade, 4);
    console.log('  ' + name + grade + '(' + cat.score + '/100)');
  }

  console.log('');

  // Quick wins
  const quickWins = getQuickWins(report.categories, 5);
  if (quickWins.length > 0) {
    console.log('  Top ' + quickWins.length + ' Quick Wins:');
    console.log('  ' + '-'.repeat(w - 4));

    for (let i = 0; i < quickWins.length; i++) {
      const qw = quickWins[i];
      const status = qw.status === 'FAIL' ? '[X]' : '[!]';
      console.log('  ' + (i + 1) + '. ' + status + ' ' + qw.name
        + ' (impact: ' + qw.impact + ', effort: ' + qw.effort + ')');
      console.log('       ' + qw.detail);
    }
  }

  console.log('');

  // Detailed results
  console.log('  Detailed Results:');
  console.log('  ' + '-'.repeat(w - 4));

  for (const [id, cat] of sorted) {
    console.log('');
    console.log('  [' + cat.grade + '] ' + cat.name + ' (' + cat.score + '/100)');

    for (const check of cat.checks) {
      const icon = statusIcon(check.status);
      console.log('    ' + icon + ' ' + check.name);
      console.log('       ' + check.detail);
    }
  }

  console.log('');
  console.log('='.repeat(w));
  console.log('  Use --format json for machine-readable output.');
  console.log('='.repeat(w));
}

/**
 * JSON reporter -- full structured output.
 */
export function reportJson(report) {
  // Clean up the report for JSON (remove cheerio $ objects)
  const clean = {
    url: report.url,
    hostname: report.hostname,
    date: report.date,
    pagesCrawled: report.pagesCrawled,
    siteProfile: report.siteProfile || null,
    overall: report.overall,
    categories: {},
    quickWins: getQuickWins(report.categories, 10),
  };

  for (const [id, cat] of Object.entries(report.categories)) {
    clean.categories[id] = {
      name: cat.name,
      score: cat.score,
      grade: cat.grade,
      weight: cat.weight,
      checks: cat.checks.map(c => ({
        id: c.id,
        name: c.name,
        status: c.status,
        detail: c.detail,
        impact: c.impact,
        effort: c.effort,
      })),
    };
  }

  return JSON.stringify(clean, null, 2);
}

function statusIcon(status) {
  switch (status) {
    case 'PASS': return '[+]';
    case 'WARN': return '[!]';
    case 'FAIL': return '[X]';
    case 'SKIP': return '[-]';
    default: return '[?]';
  }
}

function padRight(str, len) {
  while (str.length < len) str += ' ';
  return str;
}
