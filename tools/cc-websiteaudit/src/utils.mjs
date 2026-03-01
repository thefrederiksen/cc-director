import { request } from 'undici';

const USER_AGENT = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 cc-websiteaudit/1.0';
const TIMEOUT_MS = 15000;

/**
 * Raw request helper -- no redirect following.
 */
async function rawRequest(url, method = 'GET', extraHeaders = {}) {
  const resp = await request(url, {
    method,
    headers: {
      'User-Agent': USER_AGENT,
      'Accept': 'text/html,application/xhtml+xml,*/*',
      ...extraHeaders,
    },
    headersTimeout: TIMEOUT_MS,
    bodyTimeout: TIMEOUT_MS,
  });

  const status = resp.statusCode;
  const headers = flattenHeaders(resp.headers);
  const body = await resp.body.text();
  return { status, headers, body };
}

/**
 * Fetch a page, following redirects and tracking the redirect chain.
 * Returns { status, headers, html, redirectChain } or null on failure.
 */
export async function fetchPage(url) {
  const redirectChain = [];
  let currentUrl = url;
  let maxRedirects = 5;

  while (maxRedirects-- > 0) {
    try {
      const result = await rawRequest(currentUrl);

      if (result.status >= 300 && result.status < 400 && result.headers['location']) {
        const nextUrl = new URL(result.headers['location'], currentUrl).href;
        redirectChain.push({ from: currentUrl, to: nextUrl, status: result.status });
        currentUrl = nextUrl;
        continue;
      }

      return {
        status: result.status,
        headers: result.headers,
        html: result.body,
        redirectChain,
        finalUrl: currentUrl,
      };
    } catch (err) {
      return null;
    }
  }

  return null;
}

/**
 * Fetch a URL as plain text, following redirects manually.
 * Used for robots.txt, sitemap, llms.txt.
 */
export async function fetchText(url) {
  let currentUrl = url;
  let maxRedirects = 5;

  while (maxRedirects-- > 0) {
    try {
      const result = await rawRequest(currentUrl);

      if (result.status >= 300 && result.status < 400 && result.headers['location']) {
        currentUrl = new URL(result.headers['location'], currentUrl).href;
        continue;
      }

      return { status: result.status, text: result.body, headers: result.headers };
    } catch {
      return null;
    }
  }

  return null;
}

/**
 * Fetch just the headers (HEAD request), following redirects manually.
 */
export async function fetchHead(url) {
  let currentUrl = url;
  let maxRedirects = 5;

  while (maxRedirects-- > 0) {
    try {
      const result = await rawRequest(currentUrl, 'HEAD');

      if (result.status >= 300 && result.status < 400 && result.headers['location']) {
        currentUrl = new URL(result.headers['location'], currentUrl).href;
        continue;
      }

      return { status: result.status, headers: result.headers };
    } catch {
      return null;
    }
  }

  return null;
}

/**
 * Normalize a URL for deduplication.
 * - Lowercase host
 * - Remove trailing slash (except root)
 * - Remove hash
 * - Sort query params
 */
export function normalizeUrl(url) {
  try {
    const u = new URL(url);
    u.hash = '';
    // Sort search params
    u.searchParams.sort();
    let href = u.href;
    // Remove trailing slash unless it's just the origin
    if (href.endsWith('/') && u.pathname !== '/') {
      href = href.slice(0, -1);
    }
    return href;
  } catch {
    return url;
  }
}

/**
 * Check if a URL belongs to the same host.
 */
export function isSameHost(url, hostname) {
  try {
    return new URL(url).hostname === hostname;
  } catch {
    return false;
  }
}

/**
 * Sleep for the given number of milliseconds.
 */
export function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

/**
 * Flatten undici headers (which may have arrays) into simple key-value.
 */
function flattenHeaders(headers) {
  const flat = {};
  if (!headers) return flat;

  // undici returns headers as a flat object or array-based
  if (Array.isArray(headers)) {
    for (let i = 0; i < headers.length; i += 2) {
      flat[headers[i].toLowerCase()] = headers[i + 1];
    }
  } else {
    for (const [key, val] of Object.entries(headers)) {
      flat[key.toLowerCase()] = Array.isArray(val) ? val.join(', ') : val;
    }
  }
  return flat;
}

/**
 * Count words in a text string.
 */
export function countWords(text) {
  if (!text) return 0;
  return text.trim().split(/\s+/).filter(w => w.length > 0).length;
}

/**
 * Strip HTML tags and return plain text.
 */
export function stripHtml(html) {
  if (!html) return '';
  return html.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim();
}
