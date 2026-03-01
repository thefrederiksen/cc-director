import fs from 'fs';
import path from 'path';
import os from 'os';
import puppeteer from 'puppeteer-core';

const CHROME_PATHS = [
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
  process.env.CHROME_PATH,
].filter(Boolean);

function findChrome() {
  for (const p of CHROME_PATHS) {
    try {
      if (fs.statSync(p).isFile()) return p;
    } catch { /* skip */ }
  }
  return null;
}

/**
 * Convert an HTML string to a PDF file using headless Chrome.
 */
export async function htmlToPdf(html, outputPath) {
  const chromePath = findChrome();
  if (!chromePath) {
    throw new Error('Chrome not found. Install Chrome or set CHROME_PATH.');
  }

  // Write HTML to a temp file so Chrome can load it
  const tmpDir = os.tmpdir();
  const tmpHtml = path.join(tmpDir, 'cc-websiteaudit-report-' + Date.now() + '.html');
  fs.writeFileSync(tmpHtml, html, 'utf8');

  const browser = await puppeteer.launch({
    executablePath: chromePath,
    headless: true,
    args: ['--no-sandbox', '--disable-setuid-sandbox', '--disable-gpu'],
  });

  try {
    const page = await browser.newPage();
    await page.goto('file:///' + tmpHtml.replace(/\\/g, '/'), {
      waitUntil: 'networkidle2',
      timeout: 30000,
    });

    // Let styles settle
    await new Promise(r => setTimeout(r, 1000));

    await page.pdf({
      path: outputPath,
      format: 'A4',
      printBackground: true,
      margin: {
        top: '16mm',
        bottom: '16mm',
        left: '12mm',
        right: '12mm',
      },
    });
  } finally {
    await browser.close();
    // Clean up temp file
    try { fs.unlinkSync(tmpHtml); } catch { /* ignore */ }
  }
}
