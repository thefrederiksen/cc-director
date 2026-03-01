import { countWords, stripHtml } from '../utils.mjs';

/**
 * On-Page SEO analyzer.
 * Checks: title tags, meta descriptions, heading hierarchy, image alt text,
 *         internal linking, content freshness, duplicate content, Open Graph.
 */
export async function analyzeOnPageSeo(crawlResult) {
  const checks = [];

  checks.push(checkTitleTags(crawlResult));
  checks.push(checkMetaDescriptions(crawlResult));
  checks.push(checkHeadingHierarchy(crawlResult));
  checks.push(checkImageAltText(crawlResult));
  checks.push(checkInternalLinking(crawlResult));
  checks.push(checkContentLength(crawlResult));
  checks.push(checkDuplicateContent(crawlResult));
  checks.push(checkOpenGraph(crawlResult));

  return {
    name: 'On-Page SEO',
    weight: 0.20,
    checks,
  };
}

function checkTitleTags(crawl) {
  let total = crawl.pages.length;
  let missing = 0;
  let tooShort = 0;
  let tooLong = 0;
  let duplicates = 0;
  const titles = {};

  for (const page of crawl.pages) {
    const $ = page.$;
    const title = $('title').first().text().trim();

    if (!title) {
      missing++;
      continue;
    }

    if (title.length < 30) tooShort++;
    if (title.length > 60) tooLong++;

    // Track duplicates
    if (titles[title]) {
      duplicates++;
    } else {
      titles[title] = true;
    }
  }

  const issues = [];
  if (missing > 0) issues.push(missing + ' page(s) missing title tag');
  if (tooShort > 0) issues.push(tooShort + ' title(s) too short (<30 chars)');
  if (tooLong > 0) issues.push(tooLong + ' title(s) too long (>60 chars)');
  if (duplicates > 0) issues.push(duplicates + ' duplicate title(s)');

  if (issues.length === 0) {
    return { id: 'title-tags', name: 'Title Tags', status: 'PASS', detail: 'All ' + total + ' pages have properly sized, unique titles', impact: 4, effort: 1 };
  }

  return {
    id: 'title-tags',
    name: 'Title Tags',
    status: missing > 0 ? 'FAIL' : 'WARN',
    detail: issues.join('; '),
    impact: 4,
    effort: 1,
  };
}

function checkMetaDescriptions(crawl) {
  let total = crawl.pages.length;
  let missing = 0;
  let tooShort = 0;
  let tooLong = 0;
  let duplicates = 0;
  const descs = {};

  for (const page of crawl.pages) {
    const $ = page.$;
    const desc = $('meta[name="description"]').attr('content') || '';

    if (!desc.trim()) {
      missing++;
      continue;
    }

    if (desc.length < 70) tooShort++;
    if (desc.length > 160) tooLong++;

    if (descs[desc]) {
      duplicates++;
    } else {
      descs[desc] = true;
    }
  }

  const issues = [];
  if (missing > 0) issues.push(missing + ' page(s) missing meta description');
  if (tooShort > 0) issues.push(tooShort + ' description(s) too short (<70 chars)');
  if (tooLong > 0) issues.push(tooLong + ' description(s) too long (>160 chars)');
  if (duplicates > 0) issues.push(duplicates + ' duplicate description(s)');

  if (issues.length === 0) {
    return { id: 'meta-descriptions', name: 'Meta Descriptions', status: 'PASS', detail: 'All pages have unique, properly sized meta descriptions', impact: 3, effort: 1 };
  }

  return {
    id: 'meta-descriptions',
    name: 'Meta Descriptions',
    status: missing > 0 ? 'FAIL' : 'WARN',
    detail: issues.join('; '),
    impact: 3,
    effort: 1,
  };
}

function checkHeadingHierarchy(crawl) {
  let total = crawl.pages.length;
  let noH1 = 0;
  let multipleH1 = 0;
  let skippedLevels = 0;

  for (const page of crawl.pages) {
    const $ = page.$;

    // Count H1s
    const h1Count = $('h1').length;
    if (h1Count === 0) noH1++;
    if (h1Count > 1) multipleH1++;

    // Check for skipped heading levels
    const headings = [];
    $('h1, h2, h3, h4, h5, h6').each((_, el) => {
      headings.push(parseInt(el.tagName.slice(1)));
    });

    for (let i = 1; i < headings.length; i++) {
      if (headings[i] - headings[i - 1] > 1) {
        skippedLevels++;
        break; // Count once per page
      }
    }
  }

  const issues = [];
  if (noH1 > 0) issues.push(noH1 + ' page(s) missing H1');
  if (multipleH1 > 0) issues.push(multipleH1 + ' page(s) with multiple H1s');
  if (skippedLevels > 0) issues.push(skippedLevels + ' page(s) with skipped heading levels');

  if (issues.length === 0) {
    return { id: 'heading-hierarchy', name: 'Heading Hierarchy', status: 'PASS', detail: 'Proper H1-H6 structure on all pages', impact: 3, effort: 2 };
  }

  return {
    id: 'heading-hierarchy',
    name: 'Heading Hierarchy',
    status: noH1 > 0 ? 'FAIL' : 'WARN',
    detail: issues.join('; '),
    impact: 3,
    effort: 2,
  };
}

function checkImageAltText(crawl) {
  let totalImages = 0;
  let missingAlt = 0;
  let emptyAlt = 0;
  let genericAlt = 0;

  const genericPatterns = /^(image|img|photo|picture|banner|hero|logo|icon|\d+|untitled|dsc_|img_)/i;

  for (const page of crawl.pages) {
    const $ = page.$;
    $('img').each((_, el) => {
      totalImages++;
      const alt = $(el).attr('alt');

      if (alt === undefined || alt === null) {
        missingAlt++;
      } else if (alt.trim() === '') {
        // Empty alt is OK for decorative images, but we track it
        emptyAlt++;
      } else if (genericPatterns.test(alt.trim())) {
        genericAlt++;
      }
    });
  }

  if (totalImages === 0) {
    return { id: 'image-alt-text', name: 'Image Alt Text', status: 'PASS', detail: 'No images found to check', impact: 3, effort: 1 };
  }

  const issues = [];
  if (missingAlt > 0) issues.push(missingAlt + ' image(s) missing alt attribute');
  if (genericAlt > 0) issues.push(genericAlt + ' image(s) with generic alt text');

  const coverage = 1 - (missingAlt / totalImages);

  if (missingAlt === 0 && genericAlt === 0) {
    return { id: 'image-alt-text', name: 'Image Alt Text', status: 'PASS', detail: totalImages + ' images checked, all have descriptive alt text', impact: 3, effort: 1 };
  }

  return {
    id: 'image-alt-text',
    name: 'Image Alt Text',
    status: coverage < 0.5 ? 'FAIL' : 'WARN',
    detail: issues.join('; ') + ' (out of ' + totalImages + ' total)',
    impact: 3,
    effort: 1,
  };
}

function checkInternalLinking(crawl) {
  // Build a map of which pages link to which
  const inboundLinks = {};
  const outboundCounts = {};

  for (const page of crawl.pages) {
    inboundLinks[page.url] = inboundLinks[page.url] || 0;
    outboundCounts[page.url] = 0;

    const $ = page.$;
    $('a[href]').each((_, el) => {
      const href = $(el).attr('href');
      if (!href) return;
      try {
        const resolved = new URL(href, page.url).href;
        // Only count internal links
        if (new URL(resolved).hostname === crawl.hostname) {
          outboundCounts[page.url]++;
          inboundLinks[resolved] = (inboundLinks[resolved] || 0) + 1;
        }
      } catch { /* skip */ }
    });
  }

  // Find orphan pages (no inbound links except from themselves)
  const orphans = crawl.pages.filter(p => {
    const inbound = inboundLinks[p.url] || 0;
    return inbound === 0 && p.depth > 0; // Homepage naturally has 0 inbound from site crawl
  });

  // Find pages with no outbound links
  const noOutbound = crawl.pages.filter(p => (outboundCounts[p.url] || 0) === 0);

  const issues = [];
  if (orphans.length > 0) issues.push(orphans.length + ' orphan page(s) with no inbound links');
  if (noOutbound.length > 0) issues.push(noOutbound.length + ' page(s) with no outbound internal links');

  if (issues.length === 0) {
    return { id: 'internal-linking', name: 'Internal Linking', status: 'PASS', detail: 'Good internal linking structure across crawled pages', impact: 3, effort: 2 };
  }

  return {
    id: 'internal-linking',
    name: 'Internal Linking',
    status: orphans.length > 3 ? 'FAIL' : 'WARN',
    detail: issues.join('; '),
    impact: 3,
    effort: 2,
  };
}

function checkContentLength(crawl) {
  let thinPages = 0;
  const thinList = [];

  for (const page of crawl.pages) {
    const $ = page.$;
    // Get main content text (exclude nav, header, footer, scripts, styles)
    $('nav, header, footer, script, style, noscript').remove();
    const text = $('body').text();
    const words = countWords(text);

    if (words < 300) {
      thinPages++;
      thinList.push(page.url + ' (' + words + ' words)');
    }
  }

  if (thinPages === 0) {
    return { id: 'content-length', name: 'Content Length', status: 'PASS', detail: 'All pages have sufficient content depth (300+ words)', impact: 3, effort: 3 };
  }

  return {
    id: 'content-length',
    name: 'Content Length',
    status: thinPages > crawl.pages.length / 2 ? 'FAIL' : 'WARN',
    detail: thinPages + ' thin page(s) with <300 words: ' + thinList.slice(0, 3).join(', '),
    impact: 3,
    effort: 3,
  };
}

function checkDuplicateContent(crawl) {
  // Simple approach: compare text content hashes between pages
  const contentMap = {};
  let duplicates = 0;
  const dupPairs = [];

  for (const page of crawl.pages) {
    const $ = page.$;
    $('nav, header, footer, script, style, noscript').remove();
    const text = $('body').text().replace(/\s+/g, ' ').trim();

    // Simple fingerprint: first 500 chars + length
    const fingerprint = text.slice(0, 500) + '::' + text.length;

    if (contentMap[fingerprint]) {
      duplicates++;
      dupPairs.push(page.url + ' ~= ' + contentMap[fingerprint]);
    } else {
      contentMap[fingerprint] = page.url;
    }
  }

  if (duplicates === 0) {
    return { id: 'duplicate-content', name: 'Duplicate Content', status: 'PASS', detail: 'No duplicate content detected among crawled pages', impact: 3, effort: 3 };
  }

  return {
    id: 'duplicate-content',
    name: 'Duplicate Content',
    status: 'WARN',
    detail: duplicates + ' potential duplicate(s): ' + dupPairs.slice(0, 3).join('; '),
    impact: 3,
    effort: 3,
  };
}

function checkOpenGraph(crawl) {
  let total = crawl.pages.length;
  let missing = 0;
  let partial = 0;

  for (const page of crawl.pages) {
    const $ = page.$;
    const ogTitle = $('meta[property="og:title"]').attr('content');
    const ogDesc = $('meta[property="og:description"]').attr('content');
    const ogImage = $('meta[property="og:image"]').attr('content');

    if (!ogTitle && !ogDesc && !ogImage) {
      missing++;
    } else if (!ogTitle || !ogDesc || !ogImage) {
      partial++;
    }
  }

  if (missing === 0 && partial === 0) {
    return { id: 'open-graph', name: 'Open Graph / Social', status: 'PASS', detail: 'All pages have complete Open Graph tags', impact: 2, effort: 1 };
  }

  const issues = [];
  if (missing > 0) issues.push(missing + ' page(s) missing all OG tags');
  if (partial > 0) issues.push(partial + ' page(s) with incomplete OG tags');

  return {
    id: 'open-graph',
    name: 'Open Graph / Social',
    status: missing > total / 2 ? 'FAIL' : 'WARN',
    detail: issues.join('; '),
    impact: 2,
    effort: 1,
  };
}
