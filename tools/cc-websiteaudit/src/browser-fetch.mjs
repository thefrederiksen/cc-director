import puppeteer from 'puppeteer-core';
import fs from 'fs';

const CHROME_PATHS = [
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
  process.env.CHROME_PATH,
].filter(Boolean);

let browserInstance = null;

/**
 * Find Chrome on the system.
 */
function findChrome() {
  for (const p of CHROME_PATHS) {
    try {
      if (fs.statSync(p).isFile()) return p;
    } catch { /* skip */ }
  }
  return null;
}

/**
 * Launch or reuse a headless Chrome browser.
 */
async function getBrowser() {
  if (browserInstance && browserInstance.connected) return browserInstance;

  const chromePath = findChrome();
  if (!chromePath) {
    throw new Error('Chrome not found. Install Chrome or set CHROME_PATH environment variable.');
  }

  browserInstance = await puppeteer.launch({
    executablePath: chromePath,
    headless: true,
    args: [
      '--no-sandbox',
      '--disable-setuid-sandbox',
      '--disable-dev-shm-usage',
      '--disable-gpu',
    ],
  });

  return browserInstance;
}

/**
 * Fetch a page using a real browser. Used to bypass Cloudflare and JS challenges.
 * Returns { status, headers, html } or null.
 */
export async function browserFetchPage(url, timeoutMs = 30000) {
  const browser = await getBrowser();
  const page = await browser.newPage();

  try {
    await page.setUserAgent(
      'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36'
    );

    const response = await page.goto(url, {
      waitUntil: 'networkidle2',
      timeout: timeoutMs,
    });

    // Wait a moment for any late JS to finish
    await new Promise(r => setTimeout(r, 2000));

    const status = response ? response.status() : 200;
    const headers = response ? response.headers() : {};
    const html = await page.content();

    return { status, headers, html };
  } catch (err) {
    return null;
  } finally {
    await page.close();
  }
}

/**
 * Fetch a text resource (robots.txt, sitemap, llms.txt) via browser.
 */
export async function browserFetchText(url, timeoutMs = 15000) {
  const browser = await getBrowser();
  const page = await browser.newPage();

  try {
    await page.setUserAgent(
      'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36'
    );

    const response = await page.goto(url, {
      waitUntil: 'networkidle2',
      timeout: timeoutMs,
    });

    const status = response ? response.status() : 200;
    // For text files, get the raw text content
    const text = await page.evaluate(() => document.body ? document.body.innerText : '');
    const headers = response ? response.headers() : {};

    return { status, text, headers };
  } catch {
    return null;
  } finally {
    await page.close();
  }
}

/**
 * Close the browser if it's open.
 */
export async function closeBrowser() {
  if (browserInstance) {
    await browserInstance.close();
    browserInstance = null;
  }
}

/**
 * Detect if a response is a Cloudflare challenge page.
 */
export function isCloudflareChallenge(status, html) {
  if (status !== 403 && status !== 503) return false;
  if (!html) return false;
  return html.includes('Just a moment') ||
    html.includes('cf-browser-verification') ||
    html.includes('__cf_bm') ||
    html.includes('cf_clearance') ||
    (html.includes('cloudflare') && html.includes('challenge'));
}
