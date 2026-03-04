// RESULT: FAILS -- ESM import crashes at runtime due to camoufox bundling bug.
// Error: Named export 'Camoufox' not found in CJS module.
// The camoufox package declares "type": "module" but its dist/index.js is broken.
//
// Use the CJS workaround instead (see camoufox-cjs-test.cjs).
//
// Originally: tools/camoufox-test/test.mjs
// Date tested: 2026-03-04
// Usage: npm install camoufox && node camoufox-esm-test.mjs

import { Camoufox } from 'camoufox';

console.log('[INFO] Launching Camoufox...');

const browser = await Camoufox({
  headless: false,
});

const context = browser.contexts()[0] || await browser.newContext();
const page = context.pages()[0] || await context.newPage();

console.log('[INFO] Navigating to Upwork...');
await page.goto('https://www.upwork.com', { timeout: 30000 }).catch(e => console.error(`[WARN] Navigation: ${e.message}`));
console.log(`[OK] Page loaded: ${page.url()}`);
console.log('[INFO] Browser is open. Log in and test manually.');
console.log('[INFO] Press Ctrl+C to close.');

process.on('SIGINT', async () => {
  console.log('\n[INFO] Closing browser...');
  await browser.close().catch(() => {});
  console.log('[OK] Done.');
  process.exit(0);
});

await new Promise(() => {});
