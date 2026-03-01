import { countWords, stripHtml } from '../utils.mjs';

/**
 * AI Readiness analyzer.
 * Checks: llms.txt, AI crawler access, content citability, passage structure,
 *         semantic HTML, entity clarity, question coverage.
 */
export async function analyzeAiReadiness(crawlResult) {
  const checks = [];

  checks.push(checkLlmsTxt(crawlResult));
  checks.push(checkAiCrawlerAccess(crawlResult));
  checks.push(checkContentCitability(crawlResult));
  checks.push(checkPassageStructure(crawlResult));
  checks.push(checkSemanticHtml(crawlResult));
  checks.push(checkEntityClarity(crawlResult));
  checks.push(checkQuestionCoverage(crawlResult));

  return {
    name: 'AI Readiness',
    weight: 0.20,
    checks,
  };
}

// Known AI crawlers to check in robots.txt
const AI_CRAWLERS = [
  { name: 'GPTBot', operator: 'OpenAI' },
  { name: 'OAI-SearchBot', operator: 'OpenAI' },
  { name: 'ChatGPT-User', operator: 'OpenAI' },
  { name: 'ClaudeBot', operator: 'Anthropic' },
  { name: 'Claude-SearchBot', operator: 'Anthropic' },
  { name: 'PerplexityBot', operator: 'Perplexity AI' },
  { name: 'Google-Extended', operator: 'Google' },
  { name: 'Bytespider', operator: 'ByteDance' },
  { name: 'Meta-ExternalAgent', operator: 'Meta' },
  { name: 'Amazonbot', operator: 'Amazon' },
  { name: 'Applebot-Extended', operator: 'Apple' },
  { name: 'CCBot', operator: 'Common Crawl' },
];

function checkLlmsTxt(crawl) {
  const llms = crawl.llmsTxt;

  if (!llms || llms.status !== 200 || !llms.text) {
    return {
      id: 'llms-txt',
      name: 'llms.txt',
      status: 'FAIL',
      detail: 'No /llms.txt file found (only ~951 domains have one -- early mover advantage)',
      impact: 4,
      effort: 1,
    };
  }

  // Basic structure validation: should have headers (lines starting with #)
  const lines = llms.text.split('\n');
  const hasHeaders = lines.some(l => l.trim().startsWith('#'));
  const hasContent = lines.filter(l => l.trim().length > 0).length > 3;

  if (!hasHeaders || !hasContent) {
    return {
      id: 'llms-txt',
      name: 'llms.txt',
      status: 'WARN',
      detail: '/llms.txt exists but appears to have minimal content',
      impact: 4,
      effort: 1,
    };
  }

  return {
    id: 'llms-txt',
    name: 'llms.txt',
    status: 'PASS',
    detail: '/llms.txt found with structured content (' + lines.length + ' lines)',
    impact: 4,
    effort: 1,
  };
}

function checkAiCrawlerAccess(crawl) {
  const robots = crawl.robotsTxt;

  if (!robots || robots.status !== 200 || !robots.text) {
    // No robots.txt means all crawlers are allowed
    return {
      id: 'ai-crawler-access',
      name: 'AI Crawler Access',
      status: 'PASS',
      detail: 'No robots.txt found -- all AI crawlers have access by default',
      impact: 5,
      effort: 1,
    };
  }

  const blocked = [];
  const allowed = [];

  for (const crawler of AI_CRAWLERS) {
    if (isCrawlerBlocked(robots.rules, crawler.name)) {
      blocked.push(crawler.name + ' (' + crawler.operator + ')');
    } else {
      allowed.push(crawler.name);
    }
  }

  if (blocked.length === 0) {
    return {
      id: 'ai-crawler-access',
      name: 'AI Crawler Access',
      status: 'PASS',
      detail: 'All ' + AI_CRAWLERS.length + ' known AI crawlers have access',
      impact: 5,
      effort: 1,
    };
  }

  // Blocking some is a warning, blocking most is a fail
  const blockedRatio = blocked.length / AI_CRAWLERS.length;

  return {
    id: 'ai-crawler-access',
    name: 'AI Crawler Access',
    status: blockedRatio > 0.5 ? 'FAIL' : 'WARN',
    detail: blocked.length + ' AI crawler(s) blocked: ' + blocked.join(', '),
    impact: 5,
    effort: 1,
  };
}

function isCrawlerBlocked(rules, crawlerName) {
  // Check specific user-agent blocks
  for (const block of rules) {
    const ua = block.userAgent.toLowerCase();
    if (ua === crawlerName.toLowerCase() || ua === '*') {
      for (const d of block.directives) {
        if (d.type === 'disallow' && d.value === '/') {
          // If it's a specific block for this crawler, it's blocked
          if (ua === crawlerName.toLowerCase()) return true;
          // If it's a wildcard block, check if there's a specific allow override
          if (ua === '*') {
            // Check if there's a specific block that allows
            const specificBlock = rules.find(b => b.userAgent.toLowerCase() === crawlerName.toLowerCase());
            if (!specificBlock) return true; // No specific override, wildcard blocks it
          }
        }
      }
    }
  }
  return false;
}

function checkContentCitability(crawl) {
  let totalPages = crawl.pages.length;
  let citablePages = 0;
  const issues = [];

  for (const page of crawl.pages) {
    const $ = page.$;
    let isCitable = true;

    // Check 1: Does the page start with a clear answer/definition?
    // Look for answer-first formatting in the first paragraph
    const firstP = $('main p, article p, .content p, body p').first().text().trim();
    if (!firstP || countWords(firstP) < 20) {
      isCitable = false;
    }

    // Check 2: Are paragraphs self-contained (not requiring context from previous)?
    // Heuristic: paragraphs should not start with "This", "It", "They" etc.
    const paragraphs = [];
    $('p').each((_, el) => {
      const text = $(el).text().trim();
      if (countWords(text) > 10) paragraphs.push(text);
    });

    const contextDependentStarts = /^(This|It|They|These|Those|He|She|However|But|And|Also|Furthermore|Moreover)\b/;
    let contextDependent = 0;
    for (const p of paragraphs) {
      if (contextDependentStarts.test(p)) contextDependent++;
    }

    if (paragraphs.length > 0 && contextDependent / paragraphs.length > 0.5) {
      isCitable = false;
    }

    if (isCitable) citablePages++;
  }

  const ratio = totalPages > 0 ? citablePages / totalPages : 0;

  if (ratio >= 0.7) {
    return {
      id: 'content-citability',
      name: 'Content Citability',
      status: 'PASS',
      detail: citablePages + '/' + totalPages + ' pages have AI-citable content structure',
      impact: 4,
      effort: 3,
    };
  }

  return {
    id: 'content-citability',
    name: 'Content Citability',
    status: ratio < 0.3 ? 'FAIL' : 'WARN',
    detail: 'Only ' + citablePages + '/' + totalPages + ' pages have answer-first, self-contained content',
    impact: 4,
    effort: 3,
  };
}

function checkPassageStructure(crawl) {
  // Optimal passage length for AI extraction: 134-167 words
  const OPTIMAL_MIN = 100;
  const OPTIMAL_MAX = 200;
  let totalParagraphs = 0;
  let optimalParagraphs = 0;

  for (const page of crawl.pages) {
    const $ = page.$;
    $('p').each((_, el) => {
      const text = $(el).text().trim();
      const words = countWords(text);
      if (words >= 30) { // Only count substantive paragraphs
        totalParagraphs++;
        if (words >= OPTIMAL_MIN && words <= OPTIMAL_MAX) {
          optimalParagraphs++;
        }
      }
    });
  }

  if (totalParagraphs === 0) {
    return { id: 'passage-structure', name: 'Passage Structure', status: 'SKIP', detail: 'No substantive paragraphs found', impact: 3, effort: 3 };
  }

  const ratio = optimalParagraphs / totalParagraphs;

  if (ratio >= 0.4) {
    return {
      id: 'passage-structure',
      name: 'Passage Structure',
      status: 'PASS',
      detail: optimalParagraphs + '/' + totalParagraphs + ' paragraphs in optimal AI extraction range (100-200 words)',
      impact: 3,
      effort: 3,
    };
  }

  return {
    id: 'passage-structure',
    name: 'Passage Structure',
    status: ratio < 0.15 ? 'FAIL' : 'WARN',
    detail: 'Only ' + optimalParagraphs + '/' + totalParagraphs + ' paragraphs in optimal range for AI extraction',
    impact: 3,
    effort: 3,
  };
}

function checkSemanticHtml(crawl) {
  let total = crawl.pages.length;
  let semanticPages = 0;

  for (const page of crawl.pages) {
    const $ = page.$;

    // Check for semantic elements
    const hasMain = $('main').length > 0;
    const hasArticle = $('article').length > 0;
    const hasNav = $('nav').length > 0;
    const hasHeader = $('header').length > 0;
    const hasFooter = $('footer').length > 0;

    const score = [hasMain, hasArticle, hasNav, hasHeader, hasFooter].filter(Boolean).length;
    if (score >= 3) semanticPages++;
  }

  const ratio = total > 0 ? semanticPages / total : 0;

  if (ratio >= 0.7) {
    return {
      id: 'semantic-html',
      name: 'Semantic HTML',
      status: 'PASS',
      detail: semanticPages + '/' + total + ' pages use semantic HTML elements (main, article, nav, etc.)',
      impact: 3,
      effort: 2,
    };
  }

  return {
    id: 'semantic-html',
    name: 'Semantic HTML',
    status: ratio < 0.3 ? 'FAIL' : 'WARN',
    detail: 'Only ' + semanticPages + '/' + total + ' pages use semantic HTML elements',
    impact: 3,
    effort: 2,
  };
}

function checkEntityClarity(crawl) {
  const homepage = crawl.pages[0];
  if (!homepage) {
    return { id: 'entity-clarity', name: 'Entity Clarity', status: 'SKIP', detail: 'No homepage to check', impact: 4, effort: 2 };
  }

  const $ = homepage.$;
  const issues = [];

  // Check 1: Is the brand name in the title?
  const title = $('title').first().text();
  const ogSiteName = $('meta[property="og:site_name"]').attr('content');
  const brandName = ogSiteName || (title ? title.split(/[-|]/).pop().trim() : '');

  if (!brandName) {
    issues.push('Cannot determine brand name from title or og:site_name');
  }

  // Check 2: Is there a clear description of what the business does?
  const metaDesc = $('meta[name="description"]').attr('content') || '';
  const h1 = $('h1').first().text().trim();

  if (!metaDesc && !h1) {
    issues.push('No meta description or H1 to explain what this site does');
  }

  // Check 3: Are there structured data entities?
  const jsonLd = $('script[type="application/ld+json"]');
  if (jsonLd.length === 0) {
    issues.push('No structured data to help AI understand entity relationships');
  }

  if (issues.length === 0) {
    return {
      id: 'entity-clarity',
      name: 'Entity Clarity',
      status: 'PASS',
      detail: 'Brand identity clear: "' + brandName + '" with description and structured data',
      impact: 4,
      effort: 2,
    };
  }

  return {
    id: 'entity-clarity',
    name: 'Entity Clarity',
    status: issues.length > 2 ? 'FAIL' : 'WARN',
    detail: issues.join('; '),
    impact: 4,
    effort: 2,
  };
}

function checkQuestionCoverage(crawl) {
  let faqSections = 0;
  let questionHeadings = 0;

  for (const page of crawl.pages) {
    const $ = page.$;

    // Check for FAQ sections
    const text = $('body').text().toLowerCase();
    if (text.includes('frequently asked') || text.includes('faq')) {
      faqSections++;
    }

    // Check for question-format headings
    $('h2, h3, h4').each((_, el) => {
      const heading = $(el).text().trim();
      if (heading.endsWith('?') || heading.toLowerCase().startsWith('how') ||
          heading.toLowerCase().startsWith('what') || heading.toLowerCase().startsWith('why') ||
          heading.toLowerCase().startsWith('when') || heading.toLowerCase().startsWith('where') ||
          heading.toLowerCase().startsWith('can') || heading.toLowerCase().startsWith('does')) {
        questionHeadings++;
      }
    });
  }

  if (faqSections > 0 && questionHeadings >= 5) {
    return {
      id: 'question-coverage',
      name: 'Question Coverage',
      status: 'PASS',
      detail: faqSections + ' FAQ section(s) and ' + questionHeadings + ' question-format headings found',
      impact: 4,
      effort: 2,
    };
  }

  if (faqSections > 0 || questionHeadings > 0) {
    return {
      id: 'question-coverage',
      name: 'Question Coverage',
      status: 'WARN',
      detail: faqSections + ' FAQ section(s) and ' + questionHeadings + ' question headings (recommend more for AI visibility)',
      impact: 4,
      effort: 2,
    };
  }

  return {
    id: 'question-coverage',
    name: 'Question Coverage',
    status: 'FAIL',
    detail: 'No FAQ sections or question-format headings found (AI assistants favor Q&A content)',
    impact: 4,
    effort: 2,
  };
}
