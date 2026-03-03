/**
 * Explanations for categories and individual checks.
 * Each entry explains WHAT the check measures and WHY a failure matters.
 * These are shown in the report to help non-technical readers understand the findings.
 */

// ---------------------------------------------------------------------------
// Category-level explanations
// ---------------------------------------------------------------------------

export const CATEGORY_EXPLANATIONS = {
  'technical-seo': {
    what: 'How well search engines can crawl, index, and understand your site structure.',
    whyItMatters: 'If search engines cannot properly access your pages, your content will not appear in search results -- no matter how good it is.',
  },
  'on-page-seo': {
    what: 'How well individual pages are optimized for search visibility and click-through rates.',
    whyItMatters: 'Even if your pages are indexed, poor on-page optimization means they rank lower and attract fewer clicks from search results.',
  },
  'security': {
    what: 'How well your site protects visitors through security headers and encrypted connections.',
    whyItMatters: 'Security gaps can trigger browser warnings that scare away visitors, erode trust, and cause search engines to penalize your rankings.',
  },
  'structured-data': {
    what: 'Machine-readable markup (JSON-LD) that helps search engines and AI understand your content.',
    whyItMatters: 'Without structured data, you miss rich search results (star ratings, FAQs, breadcrumbs) and AI systems cannot reliably identify or cite your business.',
  },
  'ai-readiness': {
    what: 'How easily AI systems (ChatGPT, Claude, Perplexity, Google AI Overviews) can find, understand, and cite your content.',
    whyItMatters: 'AI-powered search is replacing traditional search for many users. If your content is not AI-friendly, your brand gets left out of AI-generated answers.',
  },
};

// ---------------------------------------------------------------------------
// Check-level explanations
// ---------------------------------------------------------------------------

export const CHECK_EXPLANATIONS = {

  // === Technical SEO ===

  'robots-txt': {
    what: 'The robots.txt file tells search engine crawlers which parts of your site they are allowed to access.',
    whyItMatters: 'Without a robots.txt, search engines may waste their limited crawl budget on unimportant pages, or miss your key content entirely. A misconfigured robots.txt can accidentally block your entire site from search results.',
  },
  'xml-sitemap': {
    what: 'An XML sitemap is a file that lists all the important pages on your site so search engines can discover them.',
    whyItMatters: 'Without a sitemap, search engines rely solely on following links to discover pages. New pages, pages with few incoming links, or pages deep in your site hierarchy may take weeks or months to get indexed.',
  },
  'canonicals': {
    what: 'Canonical tags tell search engines which version of a page is the "official" one when similar content exists at multiple URLs.',
    whyItMatters: 'Without canonicals, search engines may index duplicate versions of your pages, diluting your ranking power across multiple URLs instead of concentrating it on one authoritative page.',
  },
  'https': {
    what: 'HTTPS encrypts the connection between your visitors and your website, protecting data in transit.',
    whyItMatters: 'Google has confirmed HTTPS as a ranking signal. Sites without HTTPS show a "Not Secure" warning in browsers, which drives visitors away. Mixed HTTP/HTTPS content can also break functionality.',
  },
  'redirect-chains': {
    what: 'A redirect chain occurs when a URL redirects to another URL, which redirects again, creating a chain of multiple hops.',
    whyItMatters: 'Each redirect adds latency for visitors and search engines. After 3-5 hops, search engines may stop following the chain entirely, meaning the destination page never gets indexed or its ranking power is lost.',
  },
  'status-codes': {
    what: 'HTTP status codes indicate whether pages load successfully (200), have moved (301/302), or are broken (404, 500).',
    whyItMatters: 'Broken pages (404s) and server errors (500s) create dead ends for visitors and search engines. Too many errors signal to Google that your site is poorly maintained, which can lower rankings site-wide.',
  },
  'crawl-depth': {
    what: 'Crawl depth measures how many clicks it takes to reach a page from your homepage.',
    whyItMatters: 'Search engines assign more importance to pages closer to the homepage. Pages that require 4+ clicks to reach are crawled less frequently and rank lower. Important content should be within 3 clicks of your homepage.',
  },
  'url-structure': {
    what: 'URL structure evaluates whether your URLs are clean, descriptive, and follow best practices (lowercase, hyphens, no special characters).',
    whyItMatters: 'Clean URLs are easier for search engines to parse and for users to understand. URLs with random parameters, uppercase letters, or underscores can cause duplicate content issues and look less trustworthy in search results.',
  },

  // === On-Page SEO ===

  'title-tags': {
    what: 'Title tags are the clickable headlines that appear in search results and browser tabs.',
    whyItMatters: 'The title tag is the single most important on-page SEO element. Titles that are missing, too short, too long (truncated in search results), or duplicated across pages significantly reduce your click-through rate from search results.',
  },
  'meta-descriptions': {
    what: 'Meta descriptions are the summary text shown below your title in search results.',
    whyItMatters: 'While not a direct ranking factor, meta descriptions are your "ad copy" in search results. Missing or duplicate descriptions mean Google auto-generates snippets, which are often less compelling and result in fewer clicks.',
  },
  'heading-hierarchy': {
    what: 'Heading hierarchy checks that pages use H1-H6 tags in a logical, nested structure (H1 > H2 > H3, etc.).',
    whyItMatters: 'Headings help search engines understand your content structure and topic hierarchy. Pages missing an H1 or with skipped heading levels (H1 > H3) confuse search engines about what the page is primarily about.',
  },
  'image-alt-text': {
    what: 'Alt text provides a text description of images for screen readers and search engines.',
    whyItMatters: 'Search engines cannot "see" images -- they rely on alt text to understand image content. Missing alt text means your images cannot rank in image search, and your site is less accessible to visitors using screen readers.',
  },
  'internal-linking': {
    what: 'Internal linking measures how well your pages connect to each other through hyperlinks.',
    whyItMatters: 'Internal links distribute ranking power across your site and help search engines discover content. Pages with few internal links (orphan pages) get crawled less often and rank lower because search engines see them as less important.',
  },
  'content-length': {
    what: 'Content length measures the word count of your pages, flagging "thin" pages with very little text.',
    whyItMatters: 'Pages with fewer than 300 words rarely rank well because search engines see them as lacking substance. Thin content also provides little value to visitors and can be seen as low-quality, which can affect your site-wide quality signals.',
  },
  'duplicate-content': {
    what: 'Duplicate content checks for pages on your site that have substantially similar text.',
    whyItMatters: 'When multiple pages have the same content, search engines must choose which one to rank, diluting the ranking potential of all copies. In severe cases, Google may view duplicates as an attempt to manipulate rankings.',
  },
  'open-graph': {
    what: 'Open Graph tags control how your pages appear when shared on social media (Facebook, LinkedIn, Twitter/X).',
    whyItMatters: 'Without proper OG tags, social media platforms generate unpredictable previews -- wrong images, truncated titles, or missing descriptions. This reduces engagement when your content is shared, which is a significant traffic source.',
  },

  // === Security ===

  'hsts': {
    what: 'HTTP Strict Transport Security (HSTS) tells browsers to always use HTTPS when connecting to your site.',
    whyItMatters: 'Without HSTS, visitors who type your URL without "https://" are briefly connected over insecure HTTP before being redirected. This creates a window for man-in-the-middle attacks. HSTS eliminates this vulnerability.',
  },
  'csp': {
    what: 'Content Security Policy (CSP) tells browsers which sources of scripts, styles, and other resources are trusted.',
    whyItMatters: 'Without a strong CSP, your site is vulnerable to cross-site scripting (XSS) attacks where malicious scripts can steal visitor data, deface your site, or redirect visitors to phishing pages.',
  },
  'x-content-type-options': {
    what: 'The X-Content-Type-Options header prevents browsers from interpreting files as a different MIME type than declared.',
    whyItMatters: 'Without this header, browsers may "sniff" a file\'s content and execute it as a script even if it was declared as plain text. This is a common attack vector that can be easily prevented.',
  },
  'x-frame-options': {
    what: 'X-Frame-Options controls whether your site can be embedded inside a frame or iframe on another site.',
    whyItMatters: 'Without this header, attackers can overlay your site inside an invisible frame on a malicious page, tricking visitors into clicking on your site (clickjacking) to perform unintended actions.',
  },
  'referrer-policy': {
    what: 'Referrer-Policy controls how much referrer information (the URL you came from) is sent when visitors navigate away from your site.',
    whyItMatters: 'Without a referrer policy, your full page URLs (which may contain sensitive query parameters, session IDs, or private paths) are sent to every external site your visitors click through to.',
  },
  'permissions-policy': {
    what: 'Permissions-Policy (formerly Feature-Policy) controls which browser features (camera, microphone, geolocation) your site can use.',
    whyItMatters: 'Without restricting permissions, any third-party script on your site could access the visitor\'s camera, microphone, or location without your knowledge. This is especially risky if you use third-party ads or analytics.',
  },

  // === Structured Data ===

  'json-ld-present': {
    what: 'JSON-LD is the recommended format for adding structured data that search engines and AI systems can read.',
    whyItMatters: 'Sites with JSON-LD structured data are eligible for rich results in Google (star ratings, FAQ dropdowns, event details, product prices). Without it, your search listings are plain blue links competing against enhanced results from competitors.',
  },
  'organization-schema': {
    what: 'Organization schema tells search engines your business name, logo, contact info, and social profiles in a structured format.',
    whyItMatters: 'Without Organization schema, Google may display incorrect business information in knowledge panels and AI answers. This is the foundation for Google understanding your brand as a real entity rather than just a website.',
  },
  'article-schema': {
    what: 'Article/BlogPosting schema marks up your content pages with author, publish date, and topic information.',
    whyItMatters: 'Article schema makes your content eligible for Google\'s Top Stories carousel and News tab. It also helps AI systems attribute content to your authors, building authority and trust signals.',
  },
  'faq-schema': {
    what: 'FAQPage schema marks up question-and-answer content so it can appear as expandable FAQ results in Google.',
    whyItMatters: 'Pages with FAQ schema are 3.2x more likely to appear in Google AI Overviews. FAQ rich results also take up more visual space in search results, pushing competitors further down the page.',
  },
  'breadcrumb-schema': {
    what: 'BreadcrumbList schema provides a navigational trail (Home > Category > Page) that search engines display in results.',
    whyItMatters: 'Breadcrumb rich results replace the raw URL in your search listing with a readable navigation path. This looks more professional, helps users understand your site structure, and can improve click-through rates.',
  },
  'schema-validity': {
    what: 'Schema validity checks whether your structured data follows the correct format and contains required properties.',
    whyItMatters: 'Invalid structured data is ignored by search engines entirely. If your schema has errors (missing required fields, wrong types), you get zero benefit from having it -- the effort of adding it was wasted.',
  },

  // === AI Readiness ===

  'llms-txt': {
    what: 'The /llms.txt file provides AI systems with a structured overview of your site, similar to how robots.txt guides search crawlers.',
    whyItMatters: 'AI systems like ChatGPT, Claude, and Perplexity look for llms.txt to quickly understand what your site is about and what content is available. Without it, AI systems must guess, which may lead to incomplete or inaccurate representations of your business.',
  },
  'ai-crawler-access': {
    what: 'This checks whether AI-powered crawlers (GPTBot, ClaudeBot, PerplexityBot, and others) are allowed to access your content.',
    whyItMatters: 'If you block AI crawlers in robots.txt, your content cannot be included in AI-generated answers. As more people use AI for research and recommendations, blocking these crawlers means your brand disappears from a growing discovery channel.',
  },
  'content-citability': {
    what: 'Content citability measures whether your pages have self-contained, answer-first content that AI systems can easily quote.',
    whyItMatters: 'AI systems prefer content that directly answers questions in the first paragraph. If your pages bury the answer deep in the text or rely heavily on context from other pages, AI systems will cite your competitors instead.',
  },
  'passage-structure': {
    what: 'Passage structure checks whether your paragraphs are in the optimal 40-200 word range for AI extraction.',
    whyItMatters: 'AI systems extract "passages" from pages to use as citations. Paragraphs that are too short lack context; paragraphs that are too long get truncated. Well-structured passages are more likely to be selected as AI source citations.',
  },
  'semantic-html': {
    what: 'Semantic HTML means using meaningful HTML elements (article, section, nav, aside, main) instead of generic divs.',
    whyItMatters: 'AI systems use HTML semantics to understand content structure -- what is the main article vs. sidebar vs. navigation. Sites built entirely with divs force AI to guess, often leading to navigation menus or footer text being cited instead of your actual content.',
  },
  'entity-clarity': {
    what: 'Entity clarity measures how clearly your site identifies the people, organizations, and concepts it discusses using structured data.',
    whyItMatters: 'AI systems build knowledge graphs of entities and relationships. Without clear entity markup, AI cannot confidently connect your brand to its industry, location, or expertise -- so it cites more clearly identified competitors instead.',
  },
  'question-coverage': {
    what: 'Question coverage checks whether your site addresses common questions in your domain through FAQ sections or question-format headings.',
    whyItMatters: 'A large percentage of AI queries are questions. Sites that explicitly address questions (in FAQs, headings, or Q&A format) are far more likely to be selected as source citations for AI answers.',
  },
};
