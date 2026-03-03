#!/usr/bin/env node

import { Command } from 'commander';
import { crawl } from './crawler.mjs';
import { analyzeTechnicalSeo } from './analyzers/technical-seo.mjs';
import { analyzeOnPageSeo } from './analyzers/on-page-seo.mjs';
import { analyzeSecurity } from './analyzers/security.mjs';
import { analyzeStructuredData } from './analyzers/structured-data.mjs';
import { analyzeAiReadiness } from './analyzers/ai-readiness.mjs';
import { analyzeSiteProfile } from './analyzers/site-profile.mjs';
import { calculateScores, calculateOverallGrade } from './scoring.mjs';
import { reportConsole, reportJson } from './reporter.mjs';
import { reportHtml } from './reporter-html.mjs';
import { reportMarkdown } from './reporter-markdown.mjs';
import { htmlToPdf } from './reporter-pdf.mjs';
import { join } from 'path';
import { mkdirSync, writeFileSync } from 'fs';
import { createInterface } from 'readline';

function getDefaultOutputDir(toolName) {
  const home = process.env.USERPROFILE || process.env.HOME;
  const dir = join(home, 'Documents', 'cc-director', toolName);
  mkdirSync(dir, { recursive: true });
  return dir;
}

function getDefaultOutputPath(hostname, format) {
  const dir = getDefaultOutputDir('cc-websiteaudit');
  const extMap = { json: 'json', html: 'html', markdown: 'md', pdf: 'pdf' };
  const ext = extMap[format] || format;
  const date = new Date().toISOString().split('T')[0];
  const safeName = hostname.replace(/[^a-zA-Z0-9]/g, '_');
  return join(dir, `${safeName}_${date}.${ext}`);
}

async function promptOutputPath(defaultPath, quiet) {
  // Non-interactive (piped, CI, --quiet): use default silently
  if (quiet || !process.stdin.isTTY) {
    console.log('Report will be saved to: ' + defaultPath);
    return defaultPath;
  }
  const rl = createInterface({ input: process.stdin, output: process.stdout });
  return new Promise((resolve) => {
    console.log('');
    console.log('Report will be saved to:');
    console.log('  ' + defaultPath);
    console.log('');
    rl.question('Press Enter to confirm, or type a different path: ', (answer) => {
      rl.close();
      const trimmed = answer.trim();
      resolve(trimmed || defaultPath);
    });
  });
}

const program = new Command();

program
  .name('cc-websiteaudit')
  .description('Website audit for SEO, security, structured data, and AI readiness')
  .version('1.0.0')
  .argument('<url>', 'Website URL to audit')
  .option('-o, --output <path>', 'Output file path (auto-detects format from extension)')
  .option('--format <type>', 'Output format: console, json, html, markdown, pdf (default: console)', 'console')
  .option('--modules <list>', 'Comma-separated modules to run (default: all)')
  .option('--pages <number>', 'Max pages to crawl (default: 25)', v => Number(v), 25)
  .option('--depth <number>', 'Max crawl depth (default: 3)', v => Number(v), 3)
  .option('--verbose', 'Show detailed progress', false)
  .option('--quiet', 'Only show final grade', false)
  .action(run);

async function run(url, opts) {
  // Normalize URL
  if (!url.startsWith('http://') && !url.startsWith('https://')) {
    url = 'https://' + url;
  }

  let parsedUrl;
  try {
    parsedUrl = new URL(url);
  } catch {
    console.error('ERROR: Invalid URL: ' + url);
    process.exit(1);
  }

  // Determine output format from file extension if -o is provided
  let format = opts.format;
  if (opts.output && format === 'console') {
    const ext = opts.output.split('.').pop().toLowerCase();
    if (ext === 'json') format = 'json';
    else if (ext === 'html' || ext === 'htm') format = 'html';
    else if (ext === 'md' || ext === 'markdown') format = 'markdown';
    else if (ext === 'pdf') format = 'pdf';
  }

  // Default output directory when no -o specified
  if (!opts.output && format !== 'console') {
    const defaultPath = getDefaultOutputPath(parsedUrl.hostname, format);
    opts.output = await promptOutputPath(defaultPath, opts.quiet);
  }

  // Determine which modules to run
  const allModules = ['technical-seo', 'on-page-seo', 'security', 'structured-data', 'ai-readiness'];
  let modules = allModules;
  if (opts.modules) {
    modules = opts.modules.split(',').map(m => m.trim().toLowerCase());
    const invalid = modules.filter(m => !allModules.includes(m));
    if (invalid.length > 0) {
      console.error('ERROR: Unknown modules: ' + invalid.join(', '));
      console.error('Available modules: ' + allModules.join(', '));
      process.exit(1);
    }
  }

  if (!opts.quiet) {
    console.log('');
    console.log('cc-websiteaudit v1.0.0');
    console.log('='.repeat(60));
    console.log('  Target: ' + parsedUrl.hostname);
    console.log('  Pages:  up to ' + opts.pages);
    console.log('  Depth:  ' + opts.depth);
    console.log('='.repeat(60));
    console.log('');
  }

  // Phase 1: Crawl
  if (!opts.quiet) console.log('[*] Crawling site...');
  const crawlResult = await crawl(parsedUrl.href, {
    maxPages: opts.pages,
    maxDepth: opts.depth,
    verbose: opts.verbose,
  });

  if (crawlResult.pages.length === 0) {
    console.error('ERROR: Could not fetch any pages from ' + url);
    process.exit(1);
  }

  if (!opts.quiet) {
    console.log('    Crawled ' + crawlResult.pages.length + ' page(s)');
    console.log('');
  }

  // Phase 2a: Site Profile (informational, not scored)
  if (!opts.quiet) console.log('[*] Detecting site profile...');
  const siteProfile = analyzeSiteProfile(crawlResult);
  if (!opts.quiet) {
    const hosting = siteProfile.hosting.join(', ');
    const framework = siteProfile.framework.join(', ');
    console.log('    Hosting:   ' + hosting);
    console.log('    Framework: ' + framework);
    if (siteProfile.server) {
      console.log('    Server:    ' + siteProfile.server);
    }
    console.log('');
  }

  // Phase 2b: Analyze (scored)
  if (!opts.quiet) console.log('[*] Running analyzers...');

  const results = {};

  if (modules.includes('technical-seo')) {
    if (!opts.quiet) console.log('    - Technical SEO');
    results['technical-seo'] = await analyzeTechnicalSeo(crawlResult);
  }

  if (modules.includes('on-page-seo')) {
    if (!opts.quiet) console.log('    - On-Page SEO');
    results['on-page-seo'] = await analyzeOnPageSeo(crawlResult);
  }

  if (modules.includes('security')) {
    if (!opts.quiet) console.log('    - Security');
    results['security'] = await analyzeSecurity(crawlResult);
  }

  if (modules.includes('structured-data')) {
    if (!opts.quiet) console.log('    - Structured Data');
    results['structured-data'] = await analyzeStructuredData(crawlResult);
  }

  if (modules.includes('ai-readiness')) {
    if (!opts.quiet) console.log('    - AI Readiness');
    results['ai-readiness'] = await analyzeAiReadiness(crawlResult);
  }

  if (!opts.quiet) console.log('');

  // Phase 3: Score
  const scores = calculateScores(results);
  const overall = calculateOverallGrade(scores);

  // Phase 4: Report
  const report = {
    url: parsedUrl.href,
    hostname: parsedUrl.hostname,
    date: new Date().toISOString().split('T')[0],
    pagesCrawled: crawlResult.pages.length,
    siteProfile,
    overall,
    categories: scores,
    results,
  };

  if (format === 'json') {
    const json = reportJson(report);
    if (opts.output) {
      writeFileSync(opts.output, json, 'utf8');
      if (!opts.quiet) console.log('[+] JSON report saved to: ' + opts.output);
    } else {
      console.log(json);
    }
  } else if (format === 'html') {
    const html = reportHtml(report);
    if (opts.output) {
      writeFileSync(opts.output, html, 'utf8');
      if (!opts.quiet) console.log('[+] HTML report saved to: ' + opts.output);
    } else {
      console.log(html);
    }
  } else if (format === 'markdown') {
    const md = reportMarkdown(report);
    if (opts.output) {
      writeFileSync(opts.output, md, 'utf8');
      if (!opts.quiet) console.log('[+] Markdown report saved to: ' + opts.output);
    } else {
      console.log(md);
    }
  } else if (format === 'pdf') {
    let outputPath = opts.output;
    if (!outputPath) {
      const defaultPdfPath = getDefaultOutputPath(report.hostname, 'pdf');
      outputPath = await promptOutputPath(defaultPdfPath, opts.quiet);
    }
    if (!opts.quiet) console.log('[*] Generating PDF report...');
    const html = reportHtml(report);
    await htmlToPdf(html, outputPath);
    if (!opts.quiet) console.log('[+] PDF report saved to: ' + outputPath);
  } else {
    reportConsole(report, opts.quiet);
    // Auto-save both PDF and JSON reports
    if (!opts.output) {
      const defaultDir = getDefaultOutputDir('cc-websiteaudit');
      const safeName = report.hostname.replace(/[^a-zA-Z0-9]/g, '_');
      const dateStr = report.date;
      let pdfPath = join(defaultDir, safeName + '_' + dateStr + '.pdf');
      let jsonPath = join(defaultDir, safeName + '_' + dateStr + '.json');

      // In interactive mode, offer to change save location
      if (!opts.quiet && process.stdin.isTTY) {
        console.log('');
        console.log('Reports will be saved to:');
        console.log('  PDF:  ' + pdfPath);
        console.log('  JSON: ' + jsonPath);
        console.log('');
        const rl = createInterface({ input: process.stdin, output: process.stdout });
        const newDir = await new Promise((resolve) => {
          rl.question('Press Enter to confirm, or type a different directory: ', (answer) => {
            rl.close();
            resolve(answer.trim());
          });
        });
        if (newDir) {
          mkdirSync(newDir, { recursive: true });
          pdfPath = join(newDir, safeName + '_' + dateStr + '.pdf');
          jsonPath = join(newDir, safeName + '_' + dateStr + '.json');
        }
      }

      // Save JSON
      writeFileSync(jsonPath, reportJson(report), 'utf8');

      // Save PDF
      if (!opts.quiet) console.log('[*] Generating PDF report...');
      const html = reportHtml(report);
      await htmlToPdf(html, pdfPath);

      if (!opts.quiet) {
        console.log('');
        console.log('Reports saved:');
        console.log('  PDF:  ' + pdfPath);
        console.log('  JSON: ' + jsonPath);
      }
    }
  }
}

program.parse();
