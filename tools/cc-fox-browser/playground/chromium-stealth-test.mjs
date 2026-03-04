// RESULT: FAILS -- Chromium with stealth patches still gets blocked by Cloudflare Turnstile.
// Even with webdriver mask, ignoreDefaultArgs, CDP script injection, and all stealth
// patches (WebGL spoofing, canvas noise, plugin faking) -- Turnstile detects it.
//
// Conclusion: Chromium-based automation CANNOT bypass Turnstile.
// Use Camoufox instead (see camoufox-cjs-test.cjs).
//
// Originally: tools/cc-browser/upwork-stealth-test.mjs
// Date tested: 2026-03-04
// Usage: node chromium-stealth-test.mjs

import { chromium } from 'playwright-core';
import { existsSync } from 'fs';
import { join } from 'path';
import { tmpdir } from 'os';
import { mkdirSync } from 'fs';
import { rm } from 'fs/promises';

// Find Chrome
const candidates = [
  process.env.LOCALAPPDATA && join(process.env.LOCALAPPDATA, 'Google', 'Chrome', 'Application', 'chrome.exe'),
  process.env['ProgramFiles'] && join(process.env['ProgramFiles'], 'Google', 'Chrome', 'Application', 'chrome.exe'),
].filter(Boolean);
const chromePath = candidates.find(p => existsSync(p));
if (!chromePath) { console.error('Chrome not found'); process.exit(1); }

// Fresh temp profile (no lockfile conflicts)
const profileDir = join(tmpdir(), `upwork-test-${Date.now()}`);
mkdirSync(profileDir, { recursive: true });
console.log(`[INFO] Chrome: ${chromePath}`);
console.log(`[INFO] Temp profile: ${profileDir}`);
console.log(`[INFO] Launching...`);

const context = await chromium.launchPersistentContext(profileDir, {
  executablePath: chromePath,
  headless: false,
  args: [
    '--no-first-run',
    '--no-default-browser-check',
    '--disable-sync',
    '--disable-features=TranslateUI',
  ],
  ignoreDefaultArgs: [
    '--enable-automation',
    '--no-sandbox',
    '--disable-extensions',
    '--disable-background-networking',
    '--disable-client-side-phishing-detection',
    '--disable-component-extensions-with-background-pages',
    '--disable-default-apps',
    '--disable-popup-blocking',
    '--disable-infobars',
    '--enable-unsafe-swiftshader',
  ],
});

// Apply webdriver mask
await context.addInitScript(`
  Object.defineProperty(navigator, 'webdriver', { get: () => undefined, configurable: true });
  Object.defineProperty(Navigator.prototype, 'webdriver', { get: () => undefined, configurable: true });
  try { delete navigator.webdriver; } catch(e) {}
  try { delete Navigator.prototype.webdriver; } catch(e) {}
  Object.defineProperty(Navigator.prototype, 'webdriver', { get: () => undefined, configurable: true });
`);

// Also inject via CDP for existing page
const page = context.pages()[0] || await context.newPage();
try {
  const session = await context.newCDPSession(page);
  await session.send('Page.addScriptToEvaluateOnNewDocument', {
    source: `Object.defineProperty(Navigator.prototype, 'webdriver', { get: () => undefined, configurable: true });`,
  });
  await session.send('Runtime.evaluate', {
    expression: `Object.defineProperty(Navigator.prototype, 'webdriver', { get: () => undefined, configurable: true });`,
  });
  await session.detach().catch(() => {});
} catch { /* best effort */ }

console.log(`[INFO] Navigating to Upwork...`);
await page.goto('https://www.upwork.com', { timeout: 30000 }).catch(e => console.error(`[WARN] Navigation: ${e.message}`));
console.log(`[OK] Page loaded: ${page.url()}`);
console.log(`[INFO] Browser is open. Log in and test manually.`);
console.log(`[INFO] Press Ctrl+C to close.`);

// Keep alive until Ctrl+C
process.on('SIGINT', async () => {
  console.log(`\n[INFO] Closing browser...`);
  await context.close().catch(() => {});
  await rm(profileDir, { recursive: true, force: true }).catch(() => {});
  console.log(`[OK] Cleaned up. Done.`);
  process.exit(0);
});

// Prevent Node from exiting
await new Promise(() => {});
