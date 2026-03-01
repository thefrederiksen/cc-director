/**
 * Research citations keyed by check ID.
 * Each citation includes source, year, and a summary finding.
 */

export const CITATIONS = {
  // technical-seo
  'robots-txt': {
    source: 'Google Search Central',
    year: 2024,
    finding: 'A properly configured robots.txt helps search engines crawl your site efficiently and discover important content.',
  },
  'xml-sitemap': {
    source: 'Google Search Central',
    year: 2024,
    finding: 'Sites with XML sitemaps are discovered and indexed up to 3x faster by search engines.',
  },
  'canonicals': {
    source: 'Moz',
    year: 2024,
    finding: 'Canonical tags prevent duplicate content penalties and consolidate link equity to the preferred URL version.',
  },
  'https': {
    source: 'Google',
    year: 2023,
    finding: 'HTTPS is a confirmed ranking signal. Chrome marks non-HTTPS sites as "Not Secure" which increases bounce rates by up to 20%.',
  },
  'redirect-chains': {
    source: 'Screaming Frog',
    year: 2024,
    finding: 'Each redirect in a chain adds 100-500ms latency. Chains of 3+ hops can cause crawl budget waste and ranking loss.',
  },
  'status-codes': {
    source: 'Ahrefs',
    year: 2024,
    finding: '404 errors degrade user experience and waste crawl budget. Fixing broken links recovers lost link equity.',
  },
  'crawl-depth': {
    source: 'SEMrush',
    year: 2024,
    finding: 'Pages more than 3 clicks from the homepage receive 70% less crawl frequency from Googlebot.',
  },
  'url-structure': {
    source: 'Backlinko',
    year: 2024,
    finding: 'Clean, descriptive URLs with relevant keywords correlate with higher click-through rates in search results.',
  },

  // on-page-seo
  'title-tags': {
    source: 'Moz',
    year: 2024,
    finding: 'Title tags remain the single most impactful on-page SEO element. Unique, keyword-rich titles improve CTR by 20-30%.',
  },
  'meta-descriptions': {
    source: 'Ahrefs',
    year: 2024,
    finding: 'Pages with custom meta descriptions have 5.8% higher CTR than those with auto-generated snippets.',
  },
  'heading-hierarchy': {
    source: 'Google Search Central',
    year: 2024,
    finding: 'Proper heading hierarchy helps search engines understand content structure and can earn featured snippet placement.',
  },
  'image-alt-text': {
    source: 'WebAIM',
    year: 2024,
    finding: 'Alt text improves accessibility compliance and drives 10-15% of organic traffic through image search.',
  },
  'internal-linking': {
    source: 'Ahrefs',
    year: 2024,
    finding: 'Strategic internal linking distributes page authority and helps search engines discover content. Pages with more internal links rank higher.',
  },
  'content-length': {
    source: 'Backlinko',
    year: 2024,
    finding: 'The average first-page result on Google contains 1,447 words. Comprehensive content correlates with higher rankings.',
  },
  'duplicate-content': {
    source: 'Google Search Central',
    year: 2024,
    finding: 'Duplicate content dilutes ranking signals. Consolidating duplicates can improve organic traffic by 10-50%.',
  },
  'open-graph': {
    source: 'Buffer',
    year: 2024,
    finding: 'Posts with Open Graph tags get 2-3x more engagement on social platforms. Proper OG images increase CTR by 40%.',
  },

  // security
  'hsts': {
    source: 'OWASP',
    year: 2024,
    finding: 'HSTS prevents protocol downgrade attacks and cookie hijacking. Required for PCI DSS compliance.',
  },
  'csp': {
    source: 'OWASP',
    year: 2024,
    finding: 'Content Security Policy prevents XSS attacks. Sites with CSP have 94% fewer reported XSS vulnerabilities.',
  },
  'x-content-type-options': {
    source: 'Mozilla MDN',
    year: 2024,
    finding: 'The nosniff header prevents MIME-type sniffing attacks. Takes seconds to add and is part of security header best practices.',
  },
  'x-frame-options': {
    source: 'OWASP',
    year: 2024,
    finding: 'X-Frame-Options prevents clickjacking attacks. Essential for protecting authenticated user sessions.',
  },
  'referrer-policy': {
    source: 'Mozilla MDN',
    year: 2024,
    finding: 'Referrer-Policy controls information leakage. Prevents sensitive URL parameters from being sent to third parties.',
  },
  'permissions-policy': {
    source: 'W3C',
    year: 2024,
    finding: 'Permissions-Policy restricts browser features like camera, microphone, and geolocation to prevent abuse.',
  },

  // structured-data
  'json-ld-present': {
    source: 'Google Search Central',
    year: 2024,
    finding: 'JSON-LD is Google\'s recommended structured data format. Sites with structured data see 20-30% higher CTR from rich results.',
  },
  'organization-schema': {
    source: 'Schema.org',
    year: 2024,
    finding: 'Organization schema enables the Knowledge Panel and brand SERP features. Critical for brand visibility in search.',
  },
  'article-schema': {
    source: 'Google Search Central',
    year: 2024,
    finding: 'Article schema enables rich results including headline, image, and date display in search results.',
  },
  'faq-schema': {
    source: 'Search Engine Journal',
    year: 2024,
    finding: 'FAQ schema can double your SERP real estate with expandable Q&A directly in search results.',
  },
  'breadcrumb-schema': {
    source: 'Google Search Central',
    year: 2024,
    finding: 'Breadcrumb schema improves how your pages appear in search with structured navigation paths.',
  },
  'schema-validity': {
    source: 'Google Search Central',
    year: 2024,
    finding: 'Invalid schema markup is ignored by search engines. Regular validation ensures rich results remain active.',
  },

  // ai-readiness
  'llms-txt': {
    source: 'llms-txt.org',
    year: 2025,
    finding: 'llms.txt provides LLMs with structured information about your site, improving how AI systems represent your brand.',
  },
  'ai-crawler-access': {
    source: 'Originality.ai',
    year: 2025,
    finding: 'Blocking AI crawlers prevents your content from being used in AI training and responses, reducing brand visibility in AI-powered search.',
  },
  'content-citability': {
    source: 'Search Engine Land',
    year: 2025,
    finding: 'Content with clear attributable statements is 3x more likely to be cited by AI systems in generated responses.',
  },
  'passage-structure': {
    source: 'Google',
    year: 2024,
    finding: 'Well-structured passages enable Google\'s passage ranking, allowing specific sections to rank independently.',
  },
  'semantic-html': {
    source: 'W3C',
    year: 2024,
    finding: 'Semantic HTML helps machines understand content meaning. AI systems extract information more accurately from semantic markup.',
  },
  'entity-clarity': {
    source: 'Kalicube',
    year: 2025,
    finding: 'Clear entity definitions help search engines and AI build accurate Knowledge Graph entries for your brand.',
  },
  'question-coverage': {
    source: 'SEMrush',
    year: 2025,
    finding: 'Pages that directly answer common questions are 4x more likely to appear in AI-generated responses and featured snippets.',
  },
};

/**
 * Get formatted citation string for a check ID.
 */
export function getCitation(checkId) {
  const cite = CITATIONS[checkId];
  if (!cite) return '';
  return cite.source + ' (' + cite.year + ')';
}

/**
 * Get the full citation finding for a check ID.
 */
export function getCitationFinding(checkId) {
  const cite = CITATIONS[checkId];
  if (!cite) return '';
  return cite.finding + ' [' + cite.source + ', ' + cite.year + ']';
}
