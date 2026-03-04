// RESULT: WORKS -- Camoufox bypasses Cloudflare Turnstile completely.
// Logged into Upwork, full access, no challenge page.
//
// KEY FINDING: The camoufox npm package has an ESM bundling bug.
// ESM import (`import { Camoufox } from 'camoufox'`) fails at runtime.
// CJS require with explicit path works: require('./node_modules/camoufox/dist/index.cjs')
// This is the workaround used in cc-fox-browser's browser.mjs via createRequire().
//
// Originally: tools/camoufox-test/test.cjs
// Date tested: 2026-03-04
// Usage: npm install camoufox && node camoufox-cjs-test.cjs

async function main() {
  const { Camoufox } = require('./node_modules/camoufox/dist/index.cjs');

  console.log('[INFO] Launching Camoufox...');

  const browser = await Camoufox({
    headless: false,
  });

  const context = browser.contexts()[0] || await browser.newContext();
  const page = context.pages()[0] || await context.newPage();

  console.log('[INFO] Navigating to Upwork...');
  await page.goto('https://www.upwork.com', { timeout: 30000 }).catch(e => console.error('[WARN] Navigation: ' + e.message));
  console.log('[OK] Page loaded: ' + page.url());
  console.log('[INFO] Browser is open. Log in and test manually.');
  console.log('[INFO] Press Ctrl+C to close.');

  process.on('SIGINT', async () => {
    console.log('\n[INFO] Closing browser...');
    await browser.close().catch(() => {});
    console.log('[OK] Done.');
    process.exit(0);
  });

  await new Promise(() => {});
}

main().catch(e => { console.error('[FAIL]', e.message); process.exit(1); });
