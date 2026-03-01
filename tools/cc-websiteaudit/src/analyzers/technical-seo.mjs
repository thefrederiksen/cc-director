import { fetchHead, normalizeUrl } from '../utils.mjs';

/**
 * Technical SEO analyzer.
 * Checks: robots.txt, sitemap, canonicals, HTTPS, redirect chains,
 *         HTTP status codes, crawl depth, URL structure.
 */
export async function analyzeTechnicalSeo(crawlResult) {
  const checks = [];

  checks.push(checkRobotsTxt(crawlResult));
  checks.push(checkSitemap(crawlResult));
  checks.push(checkCanonicals(crawlResult));
  checks.push(checkHttps(crawlResult));
  checks.push(checkRedirectChains(crawlResult));
  checks.push(checkStatusCodes(crawlResult));
  checks.push(checkCrawlDepth(crawlResult));
  checks.push(checkUrlStructure(crawlResult));

  return {
    name: 'Technical SEO',
    weight: 0.20,
    checks,
  };
}

function checkRobotsTxt(crawl) {
  const r = crawl.robotsTxt;

  if (!r || r.status !== 200 || !r.text) {
    return {
      id: 'robots-txt',
      name: 'robots.txt',
      status: 'FAIL',
      detail: 'robots.txt not found or not accessible (HTTP ' + (r ? r.status : 'N/A') + ')',
      impact: 4,
      effort: 1,
    };
  }

  const issues = [];

  // Check for sitemap reference
  if (!r.sitemapUrls || r.sitemapUrls.length === 0) {
    issues.push('No Sitemap directive found in robots.txt');
  }

  // Check if critical resources are blocked
  const allRules = r.rules || [];
  for (const block of allRules) {
    if (block.userAgent === '*' || block.userAgent.toLowerCase() === 'googlebot') {
      for (const d of block.directives) {
        if (d.type === 'disallow') {
          if (d.value === '/') {
            issues.push('Disallow: / blocks entire site for ' + block.userAgent);
          }
          if (d.value.includes('.css') || d.value.includes('.js')) {
            issues.push('Blocking CSS/JS resources for ' + block.userAgent);
          }
        }
      }
    }
  }

  if (issues.length === 0) {
    return {
      id: 'robots-txt',
      name: 'robots.txt',
      status: 'PASS',
      detail: 'robots.txt is accessible and well-configured',
      impact: 4,
      effort: 1,
    };
  }

  return {
    id: 'robots-txt',
    name: 'robots.txt',
    status: issues.some(i => i.includes('blocks entire site')) ? 'FAIL' : 'WARN',
    detail: issues.join('; '),
    impact: 4,
    effort: 1,
  };
}

function checkSitemap(crawl) {
  if (!crawl.sitemapUrls || crawl.sitemapUrls.length === 0) {
    return {
      id: 'xml-sitemap',
      name: 'XML Sitemap',
      status: 'FAIL',
      detail: 'No XML sitemap found',
      impact: 4,
      effort: 2,
    };
  }

  // Check if sitemap URLs match crawled pages
  const crawledUrls = new Set(crawl.pages.map(p => normalizeUrl(p.url)));
  let inSitemap = 0;
  for (const sUrl of crawl.sitemapUrls) {
    if (crawledUrls.has(normalizeUrl(sUrl))) inSitemap++;
  }

  return {
    id: 'xml-sitemap',
    name: 'XML Sitemap',
    status: 'PASS',
    detail: 'Sitemap found with ' + crawl.sitemapUrls.length + ' URL(s). '
      + inSitemap + ' matched crawled pages.',
    impact: 4,
    effort: 2,
  };
}

function checkCanonicals(crawl) {
  let total = 0;
  let withCanonical = 0;
  let selfReferencing = 0;
  let conflicts = [];

  for (const page of crawl.pages) {
    total++;
    const $ = page.$;
    const canonical = $('link[rel="canonical"]').attr('href');

    if (canonical) {
      withCanonical++;
      try {
        const resolved = new URL(canonical, page.url).href;
        if (normalizeUrl(resolved) === normalizeUrl(page.url)) {
          selfReferencing++;
        } else {
          conflicts.push(page.url + ' -> ' + resolved);
        }
      } catch {
        conflicts.push(page.url + ': malformed canonical');
      }
    }
  }

  if (total === 0) {
    return { id: 'canonicals', name: 'Canonical Tags', status: 'SKIP', detail: 'No pages to check', impact: 3, effort: 2 };
  }

  const coverage = withCanonical / total;

  if (coverage === 1 && conflicts.length === 0) {
    return {
      id: 'canonicals',
      name: 'Canonical Tags',
      status: 'PASS',
      detail: 'All ' + total + ' pages have canonical tags (' + selfReferencing + ' self-referencing)',
      impact: 3,
      effort: 2,
    };
  }

  const issues = [];
  if (coverage < 1) issues.push((total - withCanonical) + ' page(s) missing canonical tag');
  if (conflicts.length > 0) issues.push(conflicts.length + ' non-self-referencing canonical(s)');

  return {
    id: 'canonicals',
    name: 'Canonical Tags',
    status: coverage < 0.5 ? 'FAIL' : 'WARN',
    detail: issues.join('; '),
    impact: 3,
    effort: 2,
  };
}

function checkHttps(crawl) {
  const url = new URL(crawl.baseUrl);
  const issues = [];

  if (url.protocol !== 'https:') {
    return {
      id: 'https',
      name: 'HTTPS',
      status: 'FAIL',
      detail: 'Site is not served over HTTPS',
      impact: 5,
      effort: 2,
    };
  }

  // Check for mixed content in crawled pages
  let mixedContentPages = 0;
  for (const page of crawl.pages) {
    const $ = page.$;
    let hasMixed = false;
    $('script[src], link[href], img[src], iframe[src]').each((_, el) => {
      const src = $(el).attr('src') || $(el).attr('href') || '';
      if (src.startsWith('http://')) {
        hasMixed = true;
      }
    });
    if (hasMixed) mixedContentPages++;
  }

  if (mixedContentPages > 0) {
    return {
      id: 'https',
      name: 'HTTPS',
      status: 'WARN',
      detail: mixedContentPages + ' page(s) have mixed content (HTTP resources on HTTPS page)',
      impact: 5,
      effort: 2,
    };
  }

  return {
    id: 'https',
    name: 'HTTPS',
    status: 'PASS',
    detail: 'Site is served over HTTPS with no mixed content detected',
    impact: 5,
    effort: 2,
  };
}

function checkRedirectChains(crawl) {
  let longChains = 0;
  let loops = 0;
  const details = [];

  for (const page of crawl.pages) {
    if (page.redirectChain && page.redirectChain.length > 2) {
      longChains++;
      details.push(page.url + ' (' + page.redirectChain.length + ' hops)');
    }

    // Detect loops (same URL appears twice in chain)
    if (page.redirectChain) {
      const urls = page.redirectChain.map(r => r.from);
      const unique = new Set(urls);
      if (unique.size < urls.length) loops++;
    }
  }

  if (longChains === 0 && loops === 0) {
    return {
      id: 'redirect-chains',
      name: 'Redirect Chains',
      status: 'PASS',
      detail: 'No long redirect chains or loops detected',
      impact: 3,
      effort: 2,
    };
  }

  const issues = [];
  if (longChains > 0) issues.push(longChains + ' page(s) with >2 redirect hops');
  if (loops > 0) issues.push(loops + ' redirect loop(s) detected');

  return {
    id: 'redirect-chains',
    name: 'Redirect Chains',
    status: loops > 0 ? 'FAIL' : 'WARN',
    detail: issues.join('; '),
    impact: 3,
    effort: 2,
  };
}

function checkStatusCodes(crawl) {
  let errors = 0;
  const errorPages = [];

  for (const page of crawl.pages) {
    if (page.status >= 400) {
      errors++;
      errorPages.push(page.url + ' (HTTP ' + page.status + ')');
    }
  }

  if (errors === 0) {
    return {
      id: 'status-codes',
      name: 'HTTP Status Codes',
      status: 'PASS',
      detail: 'All ' + crawl.pages.length + ' crawled pages returned valid status codes',
      impact: 4,
      effort: 2,
    };
  }

  return {
    id: 'status-codes',
    name: 'HTTP Status Codes',
    status: 'FAIL',
    detail: errors + ' page(s) returned 4xx/5xx: ' + errorPages.slice(0, 5).join(', '),
    impact: 4,
    effort: 2,
  };
}

function checkCrawlDepth(crawl) {
  const deepPages = crawl.pages.filter(p => p.depth > 3);

  if (deepPages.length === 0) {
    return {
      id: 'crawl-depth',
      name: 'Crawl Depth',
      status: 'PASS',
      detail: 'All pages reachable within 3 clicks of homepage',
      impact: 3,
      effort: 3,
    };
  }

  return {
    id: 'crawl-depth',
    name: 'Crawl Depth',
    status: 'WARN',
    detail: deepPages.length + ' page(s) are more than 3 clicks deep',
    impact: 3,
    effort: 3,
  };
}

function checkUrlStructure(crawl) {
  const issues = [];
  let badUrls = 0;

  for (const page of crawl.pages) {
    const u = new URL(page.url);
    const path = u.pathname;

    // Check for query parameters in URLs (non-pagination)
    if (u.search && !u.search.includes('page=')) {
      badUrls++;
    }

    // Check for uppercase in path
    if (path !== path.toLowerCase()) {
      issues.push('Uppercase in URL: ' + page.url);
      badUrls++;
    }

    // Check for underscores (hyphens preferred)
    if (path.includes('_')) {
      issues.push('Underscores in URL: ' + page.url);
      badUrls++;
    }
  }

  if (badUrls === 0) {
    return {
      id: 'url-structure',
      name: 'URL Structure',
      status: 'PASS',
      detail: 'URLs are clean, lowercase, and use hyphens',
      impact: 2,
      effort: 3,
    };
  }

  return {
    id: 'url-structure',
    name: 'URL Structure',
    status: 'WARN',
    detail: badUrls + ' URL issue(s): ' + issues.slice(0, 3).join('; '),
    impact: 2,
    effort: 3,
  };
}
