// RESULT: FAILS -- rebrowser-playwright-core also gets blocked by Cloudflare Turnstile.
// rebrowser patches some Playwright detection vectors but Turnstile still catches it.
// The fundamental issue is Chromium-based browsers are fingerprinted at the engine level.
//
// Conclusion: No Chromium-based approach works. Use Camoufox (Firefox-based).
//
// Originally: tools/turnstile-test/test.mjs
// Date tested: 2026-03-04
// Requires: npm install rebrowser-playwright-core
// Usage: node rebrowser-playwright-test.mjs

import { chromium } from 'rebrowser-playwright-core';
import { existsSync, mkdirSync } from 'fs';
import { join } from 'path';
import { tmpdir } from 'os';
import { rm } from 'fs/promises';

const candidates = [
  process.env.LOCALAPPDATA && join(process.env.LOCALAPPDATA, 'Google', 'Chrome', 'Application', 'chrome.exe'),
  process.env['ProgramFiles'] && join(process.env['ProgramFiles'], 'Google', 'Chrome', 'Application', 'chrome.exe'),
].filter(Boolean);
const chromePath = candidates.find(p => existsSync(p));
if (!chromePath) { console.error('Chrome not found'); process.exit(1); }

const profileDir = join(tmpdir(), `turnstile-test-${Date.now()}`);
mkdirSync(profileDir, { recursive: true });
console.log(`[INFO] Chrome: ${chromePath}`);
console.log(`[INFO] Temp profile: ${profileDir}`);
console.log(`[INFO] Launching with rebrowser-playwright-core...`);

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

const page = context.pages()[0] || await context.newPage();
console.log(`[INFO] Navigating to Upwork...`);
await page.goto('https://www.upwork.com', { timeout: 30000 }).catch(e => console.error(`[WARN] Navigation: ${e.message}`));
console.log(`[OK] Page loaded: ${page.url()}`);
console.log(`[INFO] Browser is open. Log in and test manually.`);
console.log(`[INFO] Press Ctrl+C to close.`);

process.on('SIGINT', async () => {
  console.log(`\n[INFO] Closing browser...`);
  await context.close().catch(() => {});
  await rm(profileDir, { recursive: true, force: true }).catch(() => {});
  console.log(`[OK] Done.`);
  process.exit(0);
});

await new Promise(() => {});
