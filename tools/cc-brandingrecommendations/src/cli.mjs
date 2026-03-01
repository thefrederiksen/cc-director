#!/usr/bin/env node

import { Command } from 'commander';
import { writeFileSync } from 'fs';
import { parseAuditFile } from './audit-parser.mjs';
import { generateTechnicalSeoRecs } from './generators/technical-seo.mjs';
import { generateOnPageSeoRecs } from './generators/on-page-seo.mjs';
import { generateSecurityRecs } from './generators/security.mjs';
import { generateStructuredDataRecs } from './generators/structured-data.mjs';
import { generateAiReadinessRecs } from './generators/ai-readiness.mjs';
import { generateContentStrategyRecs } from './generators/content-strategy.mjs';
import { generateSocialPresenceRecs } from './generators/social-presence.mjs';
import { generateBacklinkRecs } from './generators/backlinks.mjs';
import { classifyAndSort } from './scoring.mjs';
import { createWeeklyPlan } from './weekly-planner.mjs';
import { formatConsole } from './formatters/console.mjs';
import { formatJson } from './formatters/json.mjs';
import { formatMarkdown } from './formatters/markdown.mjs';

const program = new Command();

program
  .name('cc-brandingrecommendations')
  .description('Branding recommendations engine - reads cc-websiteaudit JSON and produces prioritized action plans')
  .version('1.0.0')
  .requiredOption('--audit <path>', 'Path to cc-websiteaudit JSON report')
  .option('-o, --output <path>', 'Output file path (auto-detects format from extension)')
  .option('--format <type>', 'Output format: console, json, markdown (default: console)', 'console')
  .option('--budget <level>', 'Weekly budget: low (5h), medium (10h), high (20h)', 'medium')
  .option('--industry <type>', 'Industry vertical for tailored recommendations')
  .option('--keywords <list>', 'Comma-separated target keywords')
  .option('--competitors <list>', 'Comma-separated competitor domains')
  .option('--verbose', 'Show detailed progress', false)
  .action(run);

function run(opts) {
  // Validate budget
  const validBudgets = ['low', 'medium', 'high'];
  if (!validBudgets.includes(opts.budget)) {
    console.error('ERROR: Invalid budget level: ' + opts.budget);
    console.error('Valid options: ' + validBudgets.join(', '));
    process.exit(1);
  }

  // Determine output format from file extension if -o is provided
  let format = opts.format;
  if (opts.output && format === 'console') {
    const ext = opts.output.split('.').pop().toLowerCase();
    if (ext === 'json') format = 'json';
    else if (ext === 'md') format = 'markdown';
  }

  // Validate format
  const validFormats = ['console', 'json', 'markdown'];
  if (!validFormats.includes(format)) {
    console.error('ERROR: Invalid format: ' + format);
    console.error('Valid options: ' + validFormats.join(', '));
    process.exit(1);
  }

  // Build context from options
  const context = {
    industry: opts.industry || '',
    keywords: opts.keywords || '',
    competitors: opts.competitors || '',
  };

  // Phase 1: Parse audit
  if (opts.verbose) console.log('[*] Parsing audit file: ' + opts.audit);
  let audit;
  try {
    audit = parseAuditFile(opts.audit);
  } catch (err) {
    console.error('ERROR: ' + err.message);
    process.exit(1);
  }
  if (opts.verbose) console.log('    Audit for: ' + audit.hostname + ' (score: ' + audit.overall.score + ')');

  // Phase 2: Generate recommendations
  if (opts.verbose) console.log('[*] Generating recommendations...');

  const allRecs = [];

  // Category generators (1:1 with audit categories)
  if (opts.verbose) console.log('    - Technical SEO');
  allRecs.push(...generateTechnicalSeoRecs(audit));

  if (opts.verbose) console.log('    - On-Page SEO');
  allRecs.push(...generateOnPageSeoRecs(audit));

  if (opts.verbose) console.log('    - Security');
  allRecs.push(...generateSecurityRecs(audit));

  if (opts.verbose) console.log('    - Structured Data');
  allRecs.push(...generateStructuredDataRecs(audit));

  if (opts.verbose) console.log('    - AI Readiness');
  allRecs.push(...generateAiReadinessRecs(audit));

  // Cross-cutting generators
  if (opts.verbose) console.log('    - Content Strategy');
  allRecs.push(...generateContentStrategyRecs(audit, context));

  if (opts.verbose) console.log('    - Social Presence');
  allRecs.push(...generateSocialPresenceRecs(audit, context));

  if (opts.verbose) console.log('    - Backlinks');
  allRecs.push(...generateBacklinkRecs(audit, context));

  if (opts.verbose) console.log('    Generated ' + allRecs.length + ' recommendations');

  // Phase 3: Classify and sort
  if (opts.verbose) console.log('[*] Scoring and prioritizing...');
  const sorted = classifyAndSort(allRecs);

  // Phase 4: Weekly plan
  if (opts.verbose) console.log('[*] Building weekly plan (' + opts.budget + ' budget)...');
  const plan = createWeeklyPlan(sorted, opts.budget);

  // Phase 5: Format output
  if (opts.verbose) console.log('[*] Formatting output (' + format + ')...');

  let output;
  if (format === 'json') {
    output = formatJson(plan, audit);
  } else if (format === 'markdown') {
    output = formatMarkdown(plan, audit, context);
  } else {
    output = formatConsole(plan, audit);
  }

  // Phase 6: Write output
  if (opts.output) {
    try {
      writeFileSync(opts.output, output, 'utf8');
      console.log('[+] Report saved to: ' + opts.output);
    } catch (err) {
      console.error('ERROR: Cannot write output file: ' + err.message);
      process.exit(1);
    }
  } else {
    console.log(output);
  }
}

program.parse();
