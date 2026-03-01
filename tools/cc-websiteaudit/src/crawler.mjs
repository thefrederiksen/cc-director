import * as cheerio from 'cheerio';
import { fetchPage, fetchText, normalizeUrl, isSameHost, sleep } from './utils.mjs';
import { browserFetchPage, browserFetchText, closeBrowser, isCloudflareChallenge } from './browser-fetch.mjs';

/**
 * Detect if an HTML response is a JS SPA shell with no real content.
 * These sites need browser rendering to get actual page content.
 */
function isSpaShell(html) {
  if (!html) return false;
  // Very small HTML with a JS app root and no real content
  if (html.length < 2000) {
    if (html.includes('id="root"') || html.includes('id="app"') || html.includes('id="__next"')) {
      // Check if body has almost no text content
      const bodyMatch = html.match(/<body[^>]*>([\s\S]*)<\/body>/i);
      if (bodyMatch) {
        const bodyContent = bodyMatch[1].replace(/<[^>]+>/g, '').trim();
        if (bodyContent.length < 50) return true;
      }
    }
  }
  return false;
}

/**
 * Breadth-first crawler starting from the given URL.
 * Returns an object with:
 *   - pages: array of { url, status, headers, html, $ (cheerio), depth, redirectChain }
 *   - robotsTxt: { status, text, rules }
 *   - sitemapUrls: array of URLs found in sitemap(s)
 *   - llmsTxt: { status, text } or null
 *   - baseUrl: the starting URL
 *   - hostname: the target hostname
 */
export async function crawl(startUrl, opts = {}) {
  const maxPages = opts.maxPages || 25;
  const maxDepth = opts.maxDepth || 3;
  const verbose = opts.verbose || false;
  const delayMs = opts.delayMs || 1000;

  const parsed = new URL(startUrl);
  const hostname = parsed.hostname;
  const origin = parsed.origin;

  // Track visited URLs (normalized)
  const visited = new Set();
  // Queue: [{ url, depth }]
  const queue = [{ url: normalizeUrl(startUrl), depth: 0 }];
  const pages = [];

  // Detect if we need browser mode (Cloudflare, JS SPA, or similar)
  let useBrowser = false;

  // Probe the site with a quick HTTP fetch
  const probe = await fetchPage(normalizeUrl(startUrl));
  if (probe && isCloudflareChallenge(probe.status, probe.html)) {
    useBrowser = true;
    if (!opts.quiet) {
      console.log('    [!] Cloudflare protection detected -- switching to browser mode');
    }
  } else if (probe && isSpaShell(probe.html)) {
    useBrowser = true;
    if (!opts.quiet) {
      console.log('    [!] JS-rendered SPA detected -- switching to browser mode');
    }
  }

  // Smart fetch wrappers that use browser when needed
  async function smartFetchPage(url) {
    if (useBrowser) {
      const result = await browserFetchPage(url);
      if (!result) return null;
      return { status: result.status, headers: result.headers, html: result.html, redirectChain: [] };
    }
    return fetchPage(url);
  }

  async function smartFetchText(url) {
    if (useBrowser) {
      return browserFetchText(url);
    }
    return fetchText(url);
  }

  // Fetch robots.txt
  const robotsTxt = await fetchRobotsTxt(origin, smartFetchText);

  // Fetch sitemap
  const sitemapUrls = await fetchSitemap(origin, robotsTxt, smartFetchText);

  // Fetch llms.txt
  const llmsTxt = await fetchLlmsTxt(origin, smartFetchText);

  // Crawl pages -- if we probed successfully without Cloudflare/SPA, reuse that result
  if (probe && !isCloudflareChallenge(probe.status, probe.html) && !isSpaShell(probe.html)) {
    const normalized = normalizeUrl(startUrl);
    visited.add(normalized);
    const contentType = probe.headers['content-type'] || '';
    if (contentType.includes('text/html')) {
      const $ = cheerio.load(probe.html);
      pages.push({
        url: normalized,
        status: probe.status,
        headers: probe.headers,
        html: probe.html,
        $,
        depth: 0,
        redirectChain: probe.redirectChain || [],
      });

      if (0 < maxDepth) {
        const links = extractInternalLinks($, normalized, hostname);
        for (const link of links) {
          const normLink = normalizeUrl(link);
          if (!visited.has(normLink)) {
            queue.push({ url: normLink, depth: 1 });
          }
        }
      }
    }
    // Remove the initial URL from the queue since we already processed it
    const idx = queue.findIndex(q => normalizeUrl(q.url) === normalized);
    if (idx !== -1) queue.splice(idx, 1);
  }

  // Crawl remaining pages
  while (queue.length > 0 && pages.length < maxPages) {
    const { url, depth } = queue.shift();
    const normalized = normalizeUrl(url);

    if (visited.has(normalized)) continue;
    visited.add(normalized);

    if (verbose) {
      console.log('    [crawl] depth=' + depth + ' ' + normalized);
    }

    const result = await smartFetchPage(normalized);
    if (!result) continue;

    // Only parse HTML pages
    const contentType = result.headers['content-type'] || '';
    if (!contentType.includes('text/html')) continue;

    const $ = cheerio.load(result.html);

    const page = {
      url: normalized,
      status: result.status,
      headers: result.headers,
      html: result.html,
      $,
      depth,
      redirectChain: result.redirectChain || [],
    };

    pages.push(page);

    // Extract links for next depth level
    if (depth < maxDepth) {
      const links = extractInternalLinks($, normalized, hostname);
      for (const link of links) {
        const normLink = normalizeUrl(link);
        if (!visited.has(normLink)) {
          queue.push({ url: normLink, depth: depth + 1 });
        }
      }
    }

    // Politeness delay
    if (queue.length > 0 && pages.length < maxPages) {
      await sleep(useBrowser ? delayMs * 2 : delayMs);
    }
  }

  // Close browser if we used it
  if (useBrowser) {
    await closeBrowser();
  }

  return {
    pages,
    robotsTxt,
    sitemapUrls,
    llmsTxt,
    baseUrl: startUrl,
    hostname,
    origin,
  };
}

/**
 * Extract internal links from a page.
 */
function extractInternalLinks($, pageUrl, hostname) {
  const links = new Set();
  $('a[href]').each((_, el) => {
    const href = $(el).attr('href');
    if (!href) return;

    try {
      const resolved = new URL(href, pageUrl);
      // Only follow same-host links
      if (resolved.hostname === hostname) {
        // Skip fragments, mailto, tel, javascript
        if (resolved.protocol === 'http:' || resolved.protocol === 'https:') {
          // Strip hash
          resolved.hash = '';
          links.add(resolved.href);
        }
      }
    } catch {
      // Malformed URL, skip
    }
  });
  return Array.from(links);
}

/**
 * Fetch and parse robots.txt
 */
async function fetchRobotsTxt(origin, fetcher) {
  const url = origin + '/robots.txt';
  const result = await fetcher(url);
  if (!result || result.status !== 200) {
    return { status: result ? result.status : 0, text: null, rules: [] };
  }

  const rules = parseRobotsTxt(result.text);
  return { status: 200, text: result.text, rules, sitemapUrls: extractSitemapUrls(result.text) };
}

/**
 * Parse robots.txt into an array of { userAgent, directives }
 */
function parseRobotsTxt(text) {
  const blocks = [];
  let current = null;

  for (const rawLine of text.split('\n')) {
    const line = rawLine.split('#')[0].trim();
    if (!line) continue;

    const colonIdx = line.indexOf(':');
    if (colonIdx === -1) continue;

    const key = line.slice(0, colonIdx).trim().toLowerCase();
    const value = line.slice(colonIdx + 1).trim();

    if (key === 'user-agent') {
      current = { userAgent: value, directives: [] };
      blocks.push(current);
    } else if (current) {
      current.directives.push({ type: key, value });
    }
  }

  return blocks;
}

/**
 * Extract Sitemap: directives from robots.txt
 */
function extractSitemapUrls(text) {
  const urls = [];
  for (const line of text.split('\n')) {
    const trimmed = line.split('#')[0].trim();
    if (trimmed.toLowerCase().startsWith('sitemap:')) {
      urls.push(trimmed.slice(8).trim());
    }
  }
  return urls;
}

/**
 * Fetch sitemap XML and extract URLs.
 * Checks robots.txt Sitemap directives first, then /sitemap.xml
 */
async function fetchSitemap(origin, robotsTxt, fetcher) {
  const sitemapUrls = [];
  const candidates = [];

  // From robots.txt
  if (robotsTxt.sitemapUrls && robotsTxt.sitemapUrls.length > 0) {
    candidates.push(...robotsTxt.sitemapUrls);
  } else {
    candidates.push(origin + '/sitemap.xml');
  }

  for (const url of candidates) {
    const result = await fetcher(url);
    if (!result || result.status !== 200) continue;

    // Check if it's a sitemap index
    if (result.text.includes('<sitemapindex')) {
      const $ = cheerio.load(result.text, { xmlMode: true });
      $('sitemap loc').each((_, el) => {
        // We won't recursively fetch sub-sitemaps in Phase 1,
        // just note the sitemap index exists
      });
    }

    // Extract URLs from sitemap
    const $ = cheerio.load(result.text, { xmlMode: true });
    $('url loc').each((_, el) => {
      sitemapUrls.push($(el).text().trim());
    });
  }

  return sitemapUrls;
}

/**
 * Fetch /llms.txt
 */
async function fetchLlmsTxt(origin, fetcher) {
  const url = origin + '/llms.txt';
  const result = await fetcher(url);
  if (!result || result.status !== 200) {
    return { status: result ? result.status : 0, text: null };
  }
  return { status: 200, text: result.text };
}
