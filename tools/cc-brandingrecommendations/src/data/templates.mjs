/**
 * Recommendation templates per check ID x severity level.
 *
 * Severity levels: critical, improvement, optimization, maintenance
 * Placeholders: {hostname}, {detail}, {citation}
 *
 * Each template has: title, what, why, how[], measures[]
 */

export const TEMPLATES = {
  // =========================================================================
  // TECHNICAL SEO
  // =========================================================================
  'robots-txt': {
    critical: {
      title: 'Create robots.txt File',
      what: 'Your site {hostname} is missing a robots.txt file, which means search engines have no crawl guidance.',
      why: '{citation}',
      how: [
        'Create a robots.txt file in your site root directory',
        'Add User-agent: * and Sitemap: directives',
        'Reference your XML sitemap URL in the file',
        'Test with Google Search Console robots.txt tester',
      ],
      measures: ['Verify robots.txt is accessible at {hostname}/robots.txt', 'Check Google Search Console for crawl errors'],
    },
    improvement: {
      title: 'Improve robots.txt Configuration',
      what: 'Your robots.txt exists but has configuration issues: {detail}',
      why: '{citation}',
      how: [
        'Review current robots.txt directives for accuracy',
        'Ensure critical pages are not blocked',
        'Add missing Sitemap directive if absent',
      ],
      measures: ['Verify with Google Search Console robots.txt tester'],
    },
    optimization: {
      title: 'Optimize robots.txt Directives',
      what: 'Your robots.txt is functional but could be fine-tuned for better crawl efficiency.',
      why: '{citation}',
      how: [
        'Block crawling of low-value pages (admin, search results, tag pages)',
        'Add crawl-delay for aggressive bots if needed',
      ],
      measures: ['Monitor crawl stats in Google Search Console'],
    },
    maintenance: {
      title: 'Maintain robots.txt Configuration',
      what: 'Your robots.txt is well-configured. Keep it updated as you add new sections to your site.',
      why: '{citation}',
      how: ['Review robots.txt quarterly when adding new site sections'],
      measures: ['Periodic review in Google Search Console'],
    },
  },

  'xml-sitemap': {
    critical: {
      title: 'Create XML Sitemap',
      what: 'No XML sitemap was found for {hostname}. Search engines cannot efficiently discover your pages.',
      why: '{citation}',
      how: [
        'Generate an XML sitemap with all important pages',
        'Submit the sitemap to Google Search Console',
        'Reference the sitemap URL in your robots.txt',
        'Ensure the sitemap auto-updates when content changes',
      ],
      measures: ['Verify sitemap in Google Search Console', 'Check indexed page count matches sitemap entries'],
    },
    improvement: {
      title: 'Fix XML Sitemap Issues',
      what: 'XML sitemap found but has issues: {detail}',
      why: '{citation}',
      how: [
        'Remove URLs returning 4xx/5xx status codes from sitemap',
        'Add missing important pages to the sitemap',
        'Ensure lastmod dates are accurate',
      ],
      measures: ['Re-submit sitemap to Google Search Console', 'Verify no errors in sitemap report'],
    },
    optimization: {
      title: 'Optimize XML Sitemap',
      what: 'Your sitemap is functional. Consider adding priority and change frequency hints.',
      why: '{citation}',
      how: ['Add priority values for key pages', 'Set accurate changefreq values'],
      measures: ['Monitor indexing rate in Google Search Console'],
    },
    maintenance: {
      title: 'Maintain XML Sitemap',
      what: 'Your XML sitemap is well-configured and up to date.',
      why: '{citation}',
      how: ['Verify sitemap stays current as you publish new content'],
      measures: ['Monthly check of Google Search Console sitemap report'],
    },
  },

  'canonicals': {
    critical: {
      title: 'Add Canonical Tags',
      what: 'Canonical tags are missing across your site, risking duplicate content penalties.',
      why: '{citation}',
      how: [
        'Add rel="canonical" to every page pointing to its preferred URL',
        'Ensure canonical URLs use the correct protocol (HTTPS) and domain',
        'Handle www vs non-www canonicalization consistently',
      ],
      measures: ['Crawl site with Screaming Frog to verify all pages have canonicals'],
    },
    improvement: {
      title: 'Fix Canonical Tag Issues',
      what: 'Some pages have canonical tag problems: {detail}',
      why: '{citation}',
      how: [
        'Fix self-referencing canonicals that point to wrong URLs',
        'Ensure paginated content uses correct canonical strategy',
      ],
      measures: ['Audit canonicals with a crawl tool'],
    },
    optimization: {
      title: 'Refine Canonical Strategy',
      what: 'Canonical tags are present but could be more strategic.',
      why: '{citation}',
      how: ['Consolidate thin/similar pages with cross-domain canonicals where appropriate'],
      measures: ['Monitor for canonical conflicts in Google Search Console'],
    },
    maintenance: {
      title: 'Maintain Canonical Tags',
      what: 'Canonical tags are properly configured across your site.',
      why: '{citation}',
      how: ['Review canonicals when launching new page templates'],
      measures: ['Periodic crawl audit'],
    },
  },

  'https': {
    critical: {
      title: 'Migrate to HTTPS',
      what: 'Your site is not using HTTPS. Browsers display security warnings to visitors.',
      why: '{citation}',
      how: [
        'Obtain an SSL/TLS certificate (Let\'s Encrypt is free)',
        'Install the certificate on your web server',
        'Set up 301 redirects from all HTTP URLs to HTTPS',
        'Update all internal links to use HTTPS',
        'Update Google Search Console to use HTTPS property',
      ],
      measures: ['Verify no mixed content warnings in browser console', 'Check all pages load over HTTPS'],
    },
    improvement: {
      title: 'Fix HTTPS Configuration',
      what: 'HTTPS is enabled but has issues: {detail}',
      why: '{citation}',
      how: [
        'Fix mixed content issues (HTTP resources loaded on HTTPS pages)',
        'Ensure all redirects point to HTTPS versions',
      ],
      measures: ['Scan for mixed content with browser dev tools'],
    },
    optimization: {
      title: 'Strengthen HTTPS Setup',
      what: 'HTTPS is working. Consider enabling HTTP/2 and HSTS preload.',
      why: '{citation}',
      how: ['Enable HTTP/2 on your server', 'Submit to the HSTS preload list'],
      measures: ['Test with SSL Labs (aim for A+ rating)'],
    },
    maintenance: {
      title: 'Maintain HTTPS Configuration',
      what: 'HTTPS is properly configured with no issues detected.',
      why: '{citation}',
      how: ['Monitor SSL certificate expiration dates', 'Renew certificates before expiry'],
      measures: ['Set up certificate expiry monitoring'],
    },
  },

  'redirect-chains': {
    critical: {
      title: 'Fix Redirect Chains',
      what: 'Multiple redirect chains detected, adding significant latency and wasting crawl budget.',
      why: '{citation}',
      how: [
        'Map all redirect chains using a crawl tool',
        'Update each chain to point directly to the final destination',
        'Fix any redirect loops immediately',
      ],
      measures: ['Re-crawl to verify no chains exceed 1 hop', 'Monitor server logs for redirect patterns'],
    },
    improvement: {
      title: 'Reduce Redirect Chains',
      what: 'Some redirect chains found: {detail}',
      why: '{citation}',
      how: ['Shorten chains to single hops where possible', 'Update internal links to point to final URLs'],
      measures: ['Crawl audit for redirect chains'],
    },
    optimization: {
      title: 'Optimize Redirects',
      what: 'Minor redirect issues detected. Most redirects are clean.',
      why: '{citation}',
      how: ['Review remaining redirects and consolidate where possible'],
      measures: ['Periodic redirect audit'],
    },
    maintenance: {
      title: 'Maintain Clean Redirects',
      what: 'No redirect chains detected. Redirects are clean.',
      why: '{citation}',
      how: ['Review redirects when restructuring URLs or migrating content'],
      measures: ['Quarterly redirect audit'],
    },
  },

  'status-codes': {
    critical: {
      title: 'Fix Broken Pages (4xx/5xx Errors)',
      what: 'Multiple pages returning error status codes were detected on {hostname}.',
      why: '{citation}',
      how: [
        'Identify all 404 pages from crawl data',
        'Set up 301 redirects for removed content to relevant pages',
        'Fix 500 errors by checking server logs',
        'Create a custom 404 page with navigation back to working content',
      ],
      measures: ['Monitor Google Search Console Coverage report for errors', 'Re-crawl to confirm fixes'],
    },
    improvement: {
      title: 'Fix Remaining Status Code Issues',
      what: 'Some pages have status code issues: {detail}',
      why: '{citation}',
      how: ['Fix or redirect remaining broken URLs', 'Update internal links pointing to error pages'],
      measures: ['Google Search Console error monitoring'],
    },
    optimization: {
      title: 'Fine-Tune Status Code Handling',
      what: 'Most pages return proper status codes. Minor issues remain.',
      why: '{citation}',
      how: ['Set up monitoring for new 404 errors', 'Implement proper 410 Gone for permanently removed content'],
      measures: ['Automated broken link monitoring'],
    },
    maintenance: {
      title: 'Maintain Clean Status Codes',
      what: 'All crawled pages return proper status codes.',
      why: '{citation}',
      how: ['Set up automated monitoring for new broken links'],
      measures: ['Monthly broken link check'],
    },
  },

  'crawl-depth': {
    critical: {
      title: 'Reduce Crawl Depth',
      what: 'Important pages are buried too deep in your site structure (4+ clicks from homepage).',
      why: '{citation}',
      how: [
        'Add key pages to main navigation or footer',
        'Create hub/category pages that link to deep content',
        'Flatten your site architecture to 3 levels max',
        'Add breadcrumb navigation',
      ],
      measures: ['Re-crawl to verify max depth is 3 or less', 'Check Google Search Console crawl stats'],
    },
    improvement: {
      title: 'Improve Site Depth Structure',
      what: 'Some pages are deeper than optimal: {detail}',
      why: '{citation}',
      how: ['Link deeper pages from higher-level category pages', 'Add internal links from popular pages'],
      measures: ['Crawl depth distribution analysis'],
    },
    optimization: {
      title: 'Optimize Page Depth',
      what: 'Site depth is generally good. A few pages could be more accessible.',
      why: '{citation}',
      how: ['Review analytics for underperforming deep pages and add internal links'],
      measures: ['Monitor crawl depth in regular audits'],
    },
    maintenance: {
      title: 'Maintain Flat Site Structure',
      what: 'Site structure is well-organized with good crawl depth.',
      why: '{citation}',
      how: ['Keep site architecture flat as you add new content sections'],
      measures: ['Periodic structure review'],
    },
  },

  'url-structure': {
    critical: {
      title: 'Clean Up URL Structure',
      what: 'URLs contain problematic patterns (dynamic parameters, excessive depth, non-descriptive slugs).',
      why: '{citation}',
      how: [
        'Implement clean, descriptive URL slugs',
        'Remove unnecessary query parameters from indexable URLs',
        'Use hyphens as word separators (not underscores)',
        'Set up 301 redirects from old URLs to new clean URLs',
      ],
      measures: ['Audit URLs for consistent naming patterns', 'Check Google Search Console for URL issues'],
    },
    improvement: {
      title: 'Improve URL Structure',
      what: 'URL structure has some issues: {detail}',
      why: '{citation}',
      how: ['Standardize URL patterns across the site', 'Remove unnecessary path segments'],
      measures: ['URL pattern audit'],
    },
    optimization: {
      title: 'Refine URL Patterns',
      what: 'URLs are mostly clean. Minor improvements possible.',
      why: '{citation}',
      how: ['Add keywords to URL slugs where natural', 'Ensure consistency across all URL patterns'],
      measures: ['URL readability review'],
    },
    maintenance: {
      title: 'Maintain Clean URL Structure',
      what: 'URLs are clean and well-structured.',
      why: '{citation}',
      how: ['Follow URL naming conventions when creating new content'],
      measures: ['Enforce URL standards in CMS configuration'],
    },
  },

  // =========================================================================
  // ON-PAGE SEO
  // =========================================================================
  'title-tags': {
    critical: {
      title: 'Add Unique Title Tags',
      what: 'Many pages on {hostname} are missing title tags or have duplicate titles.',
      why: '{citation}',
      how: [
        'Write unique, descriptive title tags for every page',
        'Keep titles between 50-60 characters',
        'Include primary keyword near the beginning',
        'Use format: Primary Keyword - Secondary Info | Brand Name',
      ],
      measures: ['Crawl all pages to verify unique titles', 'Monitor CTR in Google Search Console'],
    },
    improvement: {
      title: 'Optimize Title Tags',
      what: 'Title tags need improvement: {detail}',
      why: '{citation}',
      how: [
        'Fix titles that are too long (over 60 chars) or too short',
        'Remove duplicate titles across pages',
        'Add target keywords to titles missing them',
      ],
      measures: ['Track CTR changes in Google Search Console'],
    },
    optimization: {
      title: 'Fine-Tune Title Tags',
      what: 'Title tags are good. Small optimizations can improve CTR further.',
      why: '{citation}',
      how: ['A/B test title variations for top pages', 'Add power words to increase click-through rate'],
      measures: ['Monitor CTR trends by page'],
    },
    maintenance: {
      title: 'Maintain Title Tag Quality',
      what: 'Title tags are well-optimized across your site.',
      why: '{citation}',
      how: ['Create title tag templates for new content types'],
      measures: ['Include title tag review in content publishing checklist'],
    },
  },

  'meta-descriptions': {
    critical: {
      title: 'Add Meta Descriptions',
      what: 'Most pages are missing custom meta descriptions. Search engines will auto-generate snippets.',
      why: '{citation}',
      how: [
        'Write unique meta descriptions for all important pages',
        'Keep descriptions between 150-160 characters',
        'Include a call-to-action and primary keyword',
        'Start with the most important information',
      ],
      measures: ['Crawl to verify all pages have descriptions', 'Monitor CTR improvements'],
    },
    improvement: {
      title: 'Improve Meta Descriptions',
      what: 'Some meta description issues found: {detail}',
      why: '{citation}',
      how: ['Fix duplicate meta descriptions', 'Extend descriptions that are too short'],
      measures: ['CTR monitoring in Google Search Console'],
    },
    optimization: {
      title: 'Optimize Meta Descriptions',
      what: 'Meta descriptions are mostly in place. Refine for better CTR.',
      why: '{citation}',
      how: ['Add structured snippets to descriptions for top pages', 'Include emotional triggers and CTAs'],
      measures: ['A/B test description variations'],
    },
    maintenance: {
      title: 'Maintain Meta Descriptions',
      what: 'Meta descriptions are well-written across your site.',
      why: '{citation}',
      how: ['Include description writing in content publishing workflow'],
      measures: ['Periodic quality review'],
    },
  },

  'heading-hierarchy': {
    critical: {
      title: 'Fix Heading Structure',
      what: 'Pages have broken heading hierarchy (missing H1, skipped levels, multiple H1s).',
      why: '{citation}',
      how: [
        'Ensure every page has exactly one H1 tag',
        'Follow sequential order: H1 -> H2 -> H3 (no skipping)',
        'Use headings for structure, not styling',
        'Include target keywords in H1 and H2 headings naturally',
      ],
      measures: ['Validate heading hierarchy with a crawl tool', 'Check featured snippet eligibility'],
    },
    improvement: {
      title: 'Improve Heading Hierarchy',
      what: 'Heading structure has issues: {detail}',
      why: '{citation}',
      how: ['Fix pages with multiple H1 tags', 'Fill in skipped heading levels'],
      measures: ['Heading structure audit'],
    },
    optimization: {
      title: 'Optimize Heading Content',
      what: 'Heading structure is solid. Optimize heading text for relevance.',
      why: '{citation}',
      how: ['Add keyword-rich H2/H3 subheadings', 'Use question-format headings for FAQ content'],
      measures: ['Monitor featured snippet wins'],
    },
    maintenance: {
      title: 'Maintain Heading Standards',
      what: 'Heading hierarchy is properly structured.',
      why: '{citation}',
      how: ['Enforce heading standards in CMS templates'],
      measures: ['Include in content quality checklist'],
    },
  },

  'image-alt-text': {
    critical: {
      title: 'Add Image Alt Text',
      what: 'Most images on {hostname} are missing alt text, hurting accessibility and image search visibility.',
      why: '{citation}',
      how: [
        'Audit all images and add descriptive alt text',
        'Describe the image content, not just keywords',
        'Keep alt text under 125 characters',
        'Leave decorative images with empty alt="" attribute',
      ],
      measures: ['Crawl to verify alt text coverage', 'Monitor image search traffic in Google Search Console'],
    },
    improvement: {
      title: 'Improve Image Alt Text',
      what: 'Some images missing alt text: {detail}',
      why: '{citation}',
      how: ['Add alt text to remaining images', 'Review existing alt text for quality and accuracy'],
      measures: ['Image alt text coverage audit'],
    },
    optimization: {
      title: 'Optimize Image Alt Text',
      what: 'Most images have alt text. Refine for better relevance.',
      why: '{citation}',
      how: ['Include relevant keywords naturally in alt text', 'Add structured file names to images'],
      measures: ['Monitor image search traffic'],
    },
    maintenance: {
      title: 'Maintain Image Alt Text',
      what: 'Image alt text is well-maintained across your site.',
      why: '{citation}',
      how: ['Require alt text in content publishing workflow'],
      measures: ['Periodic accessibility audit'],
    },
  },

  'internal-linking': {
    critical: {
      title: 'Build Internal Link Structure',
      what: 'Poor internal linking detected. Many pages are orphaned or have very few internal links.',
      why: '{citation}',
      how: [
        'Identify orphan pages and link to them from relevant content',
        'Add contextual links within body content',
        'Create topic clusters with pillar pages linking to related content',
        'Use descriptive anchor text (not "click here")',
      ],
      measures: ['Monitor internal link distribution with crawl tool', 'Track organic traffic to previously orphaned pages'],
    },
    improvement: {
      title: 'Strengthen Internal Linking',
      what: 'Internal linking could be improved: {detail}',
      why: '{citation}',
      how: ['Add 2-3 contextual internal links per page', 'Link new content to existing relevant pages'],
      measures: ['Internal link distribution analysis'],
    },
    optimization: {
      title: 'Optimize Internal Link Strategy',
      what: 'Internal linking is decent. Strategic improvements can boost key pages.',
      why: '{citation}',
      how: ['Point more internal links to high-priority pages', 'Improve anchor text diversity'],
      measures: ['Track page authority distribution'],
    },
    maintenance: {
      title: 'Maintain Internal Links',
      what: 'Internal linking structure is healthy.',
      why: '{citation}',
      how: ['Include internal linking in content creation checklist'],
      measures: ['Quarterly internal link audit'],
    },
  },

  'content-length': {
    critical: {
      title: 'Expand Thin Content',
      what: 'Many pages have very thin content (under 300 words), providing little value to visitors or search engines.',
      why: '{citation}',
      how: [
        'Identify pages with fewer than 300 words',
        'Expand with valuable, relevant information',
        'Consider merging thin pages covering similar topics',
        'Add supporting elements: examples, data, visuals',
      ],
      measures: ['Track word count per page', 'Monitor organic traffic after content expansion'],
    },
    improvement: {
      title: 'Improve Content Depth',
      what: 'Some pages have thin content: {detail}',
      why: '{citation}',
      how: ['Expand key pages to 800+ words with quality content', 'Add FAQ sections to shorter pages'],
      measures: ['Content length audit', 'Engagement metrics monitoring'],
    },
    optimization: {
      title: 'Deepen Content Quality',
      what: 'Content length is generally adequate. Some pages could benefit from more depth.',
      why: '{citation}',
      how: ['Add expert insights, data, and examples to top-performing pages'],
      measures: ['Track time on page and scroll depth'],
    },
    maintenance: {
      title: 'Maintain Content Quality',
      what: 'Content depth is strong across your site.',
      why: '{citation}',
      how: ['Set minimum word count standards for new content'],
      measures: ['Include content depth in editorial guidelines'],
    },
  },

  'duplicate-content': {
    critical: {
      title: 'Resolve Duplicate Content',
      what: 'Significant duplicate content detected across {hostname}, diluting ranking potential.',
      why: '{citation}',
      how: [
        'Identify all duplicate/near-duplicate page pairs',
        'Implement canonical tags pointing to the preferred version',
        'Set up 301 redirects for true duplicates',
        'Rewrite near-duplicate content to be unique',
      ],
      measures: ['Crawl with duplicate content detection', 'Monitor indexed page count in Google Search Console'],
    },
    improvement: {
      title: 'Reduce Duplicate Content',
      what: 'Some duplicate content issues found: {detail}',
      why: '{citation}',
      how: ['Consolidate remaining duplicates with canonicals or redirects'],
      measures: ['Duplicate content scan'],
    },
    optimization: {
      title: 'Minimize Content Overlap',
      what: 'Minor content overlap detected. Most pages are unique.',
      why: '{citation}',
      how: ['Differentiate similar pages with unique angles and data'],
      measures: ['Periodic duplicate content audit'],
    },
    maintenance: {
      title: 'Maintain Content Uniqueness',
      what: 'No significant duplicate content issues detected.',
      why: '{citation}',
      how: ['Review new content for overlap before publishing'],
      measures: ['Include uniqueness check in editorial workflow'],
    },
  },

  'open-graph': {
    critical: {
      title: 'Add Open Graph Tags',
      what: 'No Open Graph meta tags found. Social media shares will display poorly.',
      why: '{citation}',
      how: [
        'Add og:title, og:description, og:image, og:url to every page',
        'Create social-optimized images (1200x630 pixels)',
        'Add Twitter Card meta tags (twitter:card, twitter:title, etc.)',
        'Test with Facebook Sharing Debugger and Twitter Card Validator',
      ],
      measures: ['Verify OG tags with social media debugging tools', 'Monitor social referral traffic'],
    },
    improvement: {
      title: 'Improve Open Graph Tags',
      what: 'Open Graph tags are incomplete: {detail}',
      why: '{citation}',
      how: ['Add missing OG properties', 'Create proper social sharing images'],
      measures: ['Test with Facebook Sharing Debugger'],
    },
    optimization: {
      title: 'Optimize Social Sharing',
      what: 'Basic OG tags are present. Optimize for better engagement.',
      why: '{citation}',
      how: ['Create custom social images per page', 'Optimize OG descriptions for social engagement'],
      measures: ['Track social sharing metrics'],
    },
    maintenance: {
      title: 'Maintain Open Graph Tags',
      what: 'Open Graph tags are well-configured for social sharing.',
      why: '{citation}',
      how: ['Include OG tags in content publishing templates'],
      measures: ['Periodic social sharing preview check'],
    },
  },

  // =========================================================================
  // SECURITY
  // =========================================================================
  'hsts': {
    critical: {
      title: 'Enable HSTS Header',
      what: 'HSTS (HTTP Strict Transport Security) is not configured on {hostname}.',
      why: '{citation}',
      how: [
        'Add Strict-Transport-Security header to your server configuration',
        'Start with max-age=86400 (1 day) and test',
        'Gradually increase to max-age=31536000 (1 year)',
        'Add includeSubDomains once confirmed working',
      ],
      measures: ['Verify header with securityheaders.com', 'Test with curl -I to confirm HSTS header'],
    },
    improvement: {
      title: 'Strengthen HSTS Configuration',
      what: 'HSTS is enabled but could be stronger: {detail}',
      why: '{citation}',
      how: ['Increase max-age to at least 1 year', 'Add includeSubDomains directive'],
      measures: ['Security header scan'],
    },
    optimization: {
      title: 'HSTS Preload Submission',
      what: 'HSTS is properly configured. Consider preload submission.',
      why: '{citation}',
      how: ['Add preload directive and submit to hstspreload.org'],
      measures: ['Check preload status at hstspreload.org'],
    },
    maintenance: {
      title: 'Maintain HSTS Configuration',
      what: 'HSTS is properly configured with strong settings.',
      why: '{citation}',
      how: ['Ensure HSTS header is preserved during server changes'],
      measures: ['Include in security audit checklist'],
    },
  },

  'csp': {
    critical: {
      title: 'Implement Content Security Policy',
      what: 'No Content-Security-Policy header found. Your site is vulnerable to XSS attacks.',
      why: '{citation}',
      how: [
        'Start with Content-Security-Policy-Report-Only to audit violations',
        'Define allowed sources for scripts, styles, images, and fonts',
        'Gradually tighten the policy as you identify all legitimate sources',
        'Switch to enforcing mode once stable',
      ],
      measures: ['Monitor CSP violation reports', 'Test with securityheaders.com'],
    },
    improvement: {
      title: 'Strengthen Content Security Policy',
      what: 'CSP exists but is too permissive: {detail}',
      why: '{citation}',
      how: ['Remove unsafe-inline and unsafe-eval where possible', 'Use nonces or hashes for inline scripts'],
      measures: ['CSP evaluation scan', 'Monitor violation reports'],
    },
    optimization: {
      title: 'Refine Content Security Policy',
      what: 'CSP is functional. Fine-tune for better protection.',
      why: '{citation}',
      how: ['Tighten source restrictions', 'Add frame-ancestors directive'],
      measures: ['Regular CSP audit'],
    },
    maintenance: {
      title: 'Maintain Content Security Policy',
      what: 'CSP is well-configured and enforced.',
      why: '{citation}',
      how: ['Update CSP when adding new third-party scripts or resources'],
      measures: ['Ongoing violation report monitoring'],
    },
  },

  'x-content-type-options': {
    critical: {
      title: 'Add X-Content-Type-Options Header',
      what: 'X-Content-Type-Options: nosniff header is missing from {hostname}.',
      why: '{citation}',
      how: [
        'Add X-Content-Type-Options: nosniff to your server configuration',
        'This is a single-line change in Apache, Nginx, or your CDN config',
      ],
      measures: ['Verify with curl -I or securityheaders.com'],
    },
    improvement: {
      title: 'Fix X-Content-Type-Options',
      what: 'X-Content-Type-Options header has issues: {detail}',
      why: '{citation}',
      how: ['Set the header value to exactly "nosniff"'],
      measures: ['Header verification scan'],
    },
    optimization: {
      title: 'Verify X-Content-Type-Options',
      what: 'Header is set. Verify it is applied consistently across all responses.',
      why: '{citation}',
      how: ['Check header on different content types and paths'],
      measures: ['Full-site header audit'],
    },
    maintenance: {
      title: 'Maintain X-Content-Type-Options',
      what: 'X-Content-Type-Options is properly configured.',
      why: '{citation}',
      how: ['Ensure header is preserved during server configuration changes'],
      measures: ['Include in security header checklist'],
    },
  },

  'x-frame-options': {
    critical: {
      title: 'Add X-Frame-Options Header',
      what: 'X-Frame-Options header is missing, leaving your site vulnerable to clickjacking.',
      why: '{citation}',
      how: [
        'Add X-Frame-Options: DENY or SAMEORIGIN to server configuration',
        'Use SAMEORIGIN if your site uses iframes internally',
        'Use DENY if no framing is needed',
      ],
      measures: ['Verify with securityheaders.com'],
    },
    improvement: {
      title: 'Fix X-Frame-Options',
      what: 'X-Frame-Options is set but may need adjustment: {detail}',
      why: '{citation}',
      how: ['Review and correct the X-Frame-Options value'],
      measures: ['Header verification'],
    },
    optimization: {
      title: 'Migrate to CSP frame-ancestors',
      what: 'X-Frame-Options works but CSP frame-ancestors is the modern replacement.',
      why: '{citation}',
      how: ['Add frame-ancestors directive to your CSP alongside X-Frame-Options'],
      measures: ['Verify both headers are present'],
    },
    maintenance: {
      title: 'Maintain Clickjacking Protection',
      what: 'X-Frame-Options is properly configured.',
      why: '{citation}',
      how: ['Maintain header during server changes'],
      measures: ['Security header audit'],
    },
  },

  'referrer-policy': {
    critical: {
      title: 'Add Referrer-Policy Header',
      what: 'No Referrer-Policy header found. Your site may leak sensitive URL information.',
      why: '{citation}',
      how: [
        'Add Referrer-Policy: strict-origin-when-cross-origin to server configuration',
        'This is the recommended default balancing privacy and analytics needs',
      ],
      measures: ['Verify with securityheaders.com', 'Check analytics data is still flowing'],
    },
    improvement: {
      title: 'Improve Referrer-Policy',
      what: 'Referrer-Policy could be stricter: {detail}',
      why: '{citation}',
      how: ['Upgrade to strict-origin-when-cross-origin or stricter'],
      measures: ['Header verification'],
    },
    optimization: {
      title: 'Optimize Referrer-Policy',
      what: 'Referrer-Policy is set. Consider no-referrer for sensitive pages.',
      why: '{citation}',
      how: ['Apply stricter policy to pages with sensitive URL parameters'],
      measures: ['Per-page policy audit'],
    },
    maintenance: {
      title: 'Maintain Referrer-Policy',
      what: 'Referrer-Policy is properly configured.',
      why: '{citation}',
      how: ['Maintain policy during server changes'],
      measures: ['Include in security checklist'],
    },
  },

  'permissions-policy': {
    critical: {
      title: 'Add Permissions-Policy Header',
      what: 'No Permissions-Policy header found. Browser features are unrestricted.',
      why: '{citation}',
      how: [
        'Add Permissions-Policy header restricting unnecessary features',
        'Disable camera, microphone, geolocation if not used',
        'Example: Permissions-Policy: camera=(), microphone=(), geolocation=()',
      ],
      measures: ['Verify with securityheaders.com'],
    },
    improvement: {
      title: 'Strengthen Permissions-Policy',
      what: 'Permissions-Policy exists but is too permissive: {detail}',
      why: '{citation}',
      how: ['Restrict additional unused browser features'],
      measures: ['Feature usage audit'],
    },
    optimization: {
      title: 'Refine Permissions-Policy',
      what: 'Policy is set. Review for any features that can be further restricted.',
      why: '{citation}',
      how: ['Audit which browser APIs are actually used and restrict the rest'],
      measures: ['Browser feature audit'],
    },
    maintenance: {
      title: 'Maintain Permissions-Policy',
      what: 'Permissions-Policy is properly configured.',
      why: '{citation}',
      how: ['Update policy when adding features that require browser permissions'],
      measures: ['Include in security checklist'],
    },
  },

  // =========================================================================
  // STRUCTURED DATA
  // =========================================================================
  'json-ld-present': {
    critical: {
      title: 'Add JSON-LD Structured Data',
      what: 'No structured data found on {hostname}. You are missing rich result opportunities.',
      why: '{citation}',
      how: [
        'Add JSON-LD script tags to your pages',
        'Start with Organization schema on the homepage',
        'Add WebPage schema to all pages',
        'Test with Google Rich Results Test tool',
      ],
      measures: ['Validate with Google Rich Results Test', 'Monitor rich results in Google Search Console'],
    },
    improvement: {
      title: 'Expand Structured Data',
      what: 'Some structured data found but coverage is limited: {detail}',
      why: '{citation}',
      how: ['Add structured data to all page types', 'Implement missing schema types'],
      measures: ['Rich Results Test validation', 'Schema coverage audit'],
    },
    optimization: {
      title: 'Enhance Structured Data',
      what: 'Good structured data foundation. Add more schema types for richer results.',
      why: '{citation}',
      how: ['Add FAQ, HowTo, or Review schema where applicable'],
      measures: ['Monitor new rich result types in Search Console'],
    },
    maintenance: {
      title: 'Maintain Structured Data',
      what: 'Comprehensive structured data is in place.',
      why: '{citation}',
      how: ['Validate structured data when updating page templates'],
      measures: ['Monthly Rich Results Test check'],
    },
  },

  'organization-schema': {
    critical: {
      title: 'Add Organization Schema',
      what: 'No Organization schema found. Your brand identity is not machine-readable.',
      why: '{citation}',
      how: [
        'Add Organization JSON-LD to your homepage',
        'Include: name, url, logo, sameAs (social profiles), contactPoint',
        'Add founding date, description, and area served if applicable',
      ],
      measures: ['Validate with Google Rich Results Test', 'Monitor Knowledge Panel appearance'],
    },
    improvement: {
      title: 'Complete Organization Schema',
      what: 'Organization schema is incomplete: {detail}',
      why: '{citation}',
      how: ['Add missing properties: sameAs links, contactPoint, logo URL'],
      measures: ['Schema validation'],
    },
    optimization: {
      title: 'Enrich Organization Schema',
      what: 'Organization schema is present. Add optional enrichment properties.',
      why: '{citation}',
      how: ['Add department information, awards, certifications'],
      measures: ['Monitor Knowledge Panel completeness'],
    },
    maintenance: {
      title: 'Maintain Organization Schema',
      what: 'Organization schema is comprehensive and accurate.',
      why: '{citation}',
      how: ['Update schema when company information changes'],
      measures: ['Quarterly accuracy review'],
    },
  },

  'article-schema': {
    critical: {
      title: 'Add Article Schema',
      what: 'Blog posts and articles lack Article/BlogPosting schema markup.',
      why: '{citation}',
      how: [
        'Add Article or BlogPosting JSON-LD to content pages',
        'Include: headline, author, datePublished, dateModified, image',
        'Use NewsArticle for news content',
      ],
      measures: ['Validate with Google Rich Results Test', 'Monitor article rich results'],
    },
    improvement: {
      title: 'Fix Article Schema',
      what: 'Article schema found but has issues: {detail}',
      why: '{citation}',
      how: ['Add missing required properties', 'Fix date formatting issues'],
      measures: ['Schema validation per page'],
    },
    optimization: {
      title: 'Enhance Article Schema',
      what: 'Article schema is present. Add recommended properties for richer results.',
      why: '{citation}',
      how: ['Add author schema with sameAs links', 'Include wordCount and articleSection'],
      measures: ['Rich result enhancement tracking'],
    },
    maintenance: {
      title: 'Maintain Article Schema',
      what: 'Article schema is well-implemented.',
      why: '{citation}',
      how: ['Ensure CMS auto-generates Article schema for new posts'],
      measures: ['Include in content template validation'],
    },
  },

  'faq-schema': {
    critical: {
      title: 'Add FAQ Schema',
      what: 'No FAQ schema found. You are missing opportunities for expanded SERP listings.',
      why: '{citation}',
      how: [
        'Identify pages with FAQ-style content',
        'Add FAQPage JSON-LD with Question and AcceptedAnswer pairs',
        'Keep answers concise but complete',
        'Maximum 10 Q&A pairs per page recommended',
      ],
      measures: ['Validate with Google Rich Results Test', 'Monitor FAQ rich result appearances'],
    },
    improvement: {
      title: 'Expand FAQ Schema',
      what: 'FAQ schema is limited: {detail}',
      why: '{citation}',
      how: ['Add FAQ schema to more relevant pages', 'Expand Q&A coverage'],
      measures: ['FAQ coverage audit'],
    },
    optimization: {
      title: 'Optimize FAQ Content',
      what: 'FAQ schema is in place. Optimize the questions for search intent.',
      why: '{citation}',
      how: ['Align FAQ questions with common search queries', 'Add schema to high-traffic pages'],
      measures: ['Monitor FAQ rich result CTR'],
    },
    maintenance: {
      title: 'Maintain FAQ Schema',
      what: 'FAQ schema is well-implemented.',
      why: '{citation}',
      how: ['Update FAQ content when questions change', 'Add new Q&A as topics emerge'],
      measures: ['Quarterly FAQ relevance review'],
    },
  },

  'breadcrumb-schema': {
    critical: {
      title: 'Add Breadcrumb Schema',
      what: 'No breadcrumb markup found on {hostname}.',
      why: '{citation}',
      how: [
        'Add BreadcrumbList JSON-LD to all pages',
        'Include proper hierarchy from home to current page',
        'Implement visual breadcrumbs matching the schema',
      ],
      measures: ['Validate with Google Rich Results Test', 'Check breadcrumb display in search results'],
    },
    improvement: {
      title: 'Fix Breadcrumb Schema',
      what: 'Breadcrumb schema has issues: {detail}',
      why: '{citation}',
      how: ['Fix hierarchy ordering', 'Ensure URLs in breadcrumbs are correct'],
      measures: ['Schema validation'],
    },
    optimization: {
      title: 'Enhance Breadcrumb Schema',
      what: 'Breadcrumbs are functional. Ensure they cover all page types.',
      why: '{citation}',
      how: ['Add breadcrumbs to any remaining page templates'],
      measures: ['Breadcrumb coverage audit'],
    },
    maintenance: {
      title: 'Maintain Breadcrumb Schema',
      what: 'Breadcrumb schema is properly implemented.',
      why: '{citation}',
      how: ['Update breadcrumbs when reorganizing site structure'],
      measures: ['Include in template maintenance'],
    },
  },

  'schema-validity': {
    critical: {
      title: 'Fix Invalid Schema Markup',
      what: 'Structured data validation errors detected. Invalid schema is ignored by search engines.',
      why: '{citation}',
      how: [
        'Run all pages through Google Rich Results Test',
        'Fix missing required properties flagged as errors',
        'Correct data type mismatches (strings vs arrays, etc.)',
        'Remove deprecated schema properties',
      ],
      measures: ['Zero errors in Rich Results Test', 'Monitor Search Console structured data report'],
    },
    improvement: {
      title: 'Reduce Schema Errors',
      what: 'Some schema validation issues: {detail}',
      why: '{citation}',
      how: ['Fix remaining validation warnings and errors'],
      measures: ['Validation pass rate tracking'],
    },
    optimization: {
      title: 'Resolve Schema Warnings',
      what: 'Schema is valid but has recommendations/warnings.',
      why: '{citation}',
      how: ['Address recommended properties to maximize rich result eligibility'],
      measures: ['Zero warnings in validation'],
    },
    maintenance: {
      title: 'Maintain Schema Validity',
      what: 'Structured data is valid with no errors.',
      why: '{citation}',
      how: ['Validate schema changes before deployment'],
      measures: ['Include schema validation in CI/CD pipeline'],
    },
  },

  // =========================================================================
  // AI READINESS
  // =========================================================================
  'llms-txt': {
    critical: {
      title: 'Create llms.txt File',
      what: '{hostname} has no llms.txt file. AI systems have no structured way to learn about your brand.',
      why: '{citation}',
      how: [
        'Create an llms.txt file in your site root',
        'Include company name, description, key offerings, and contact info',
        'List important URLs and content categories',
        'Follow the llms-txt.org specification',
      ],
      measures: ['Verify llms.txt is accessible at {hostname}/llms.txt', 'Test with AI chatbots to see brand representation'],
    },
    improvement: {
      title: 'Improve llms.txt Content',
      what: 'llms.txt exists but is incomplete: {detail}',
      why: '{citation}',
      how: ['Add missing sections per the llms-txt.org spec', 'Include key products and differentiators'],
      measures: ['Validate against llms-txt.org specification'],
    },
    optimization: {
      title: 'Optimize llms.txt',
      what: 'llms.txt is functional. Add more detail for better AI representation.',
      why: '{citation}',
      how: ['Add detailed product/service descriptions', 'Include common questions and answers about your brand'],
      measures: ['Test AI chatbot responses about your brand'],
    },
    maintenance: {
      title: 'Maintain llms.txt',
      what: 'llms.txt is well-configured.',
      why: '{citation}',
      how: ['Update llms.txt when offerings or key information changes'],
      measures: ['Quarterly content accuracy review'],
    },
  },

  'ai-crawler-access': {
    critical: {
      title: 'Allow AI Crawler Access',
      what: 'AI crawlers are blocked from accessing {hostname}. Your content will not appear in AI-generated responses.',
      why: '{citation}',
      how: [
        'Review robots.txt for AI crawler blocks (GPTBot, ClaudeBot, etc.)',
        'Remove or modify User-agent blocks for AI crawlers you want to allow',
        'Consider which AI platforms align with your visibility goals',
      ],
      measures: ['Check robots.txt for AI crawler directives', 'Monitor AI search referral traffic'],
    },
    improvement: {
      title: 'Expand AI Crawler Access',
      what: 'Some AI crawlers are blocked: {detail}',
      why: '{citation}',
      how: ['Selectively allow additional AI crawlers', 'Balance training data usage vs. visibility goals'],
      measures: ['Review AI crawler access policies quarterly'],
    },
    optimization: {
      title: 'Fine-Tune AI Crawler Access',
      what: 'Most AI crawlers have access. Consider fine-tuning which sections they can access.',
      why: '{citation}',
      how: ['Use robots.txt to guide AI crawlers to your best content'],
      measures: ['Monitor AI-driven referral traffic'],
    },
    maintenance: {
      title: 'Maintain AI Crawler Access',
      what: 'AI crawler access is well-configured.',
      why: '{citation}',
      how: ['Review policy when new major AI crawlers emerge'],
      measures: ['Stay current on AI crawler landscape'],
    },
  },

  'content-citability': {
    critical: {
      title: 'Make Content Citable by AI',
      what: 'Content lacks clear, attributable statements that AI systems can cite.',
      why: '{citation}',
      how: [
        'Write clear, factual statements with supporting data',
        'Use definitive language ("X is Y" not "X might be Y")',
        'Include original data, statistics, and expert quotes',
        'Add author attribution and publication dates to all content',
      ],
      measures: ['Test if AI systems cite your content', 'Monitor AI-driven referral traffic'],
    },
    improvement: {
      title: 'Improve Content Citability',
      what: 'Content is partially citable: {detail}',
      why: '{citation}',
      how: ['Add data-backed claims to key pages', 'Include clear author and date attribution'],
      measures: ['AI citation monitoring'],
    },
    optimization: {
      title: 'Optimize for AI Citations',
      what: 'Good citability foundation. Enhance with more original data.',
      why: '{citation}',
      how: ['Publish original research and data', 'Create definitive guides on key topics'],
      measures: ['Track AI citation volume'],
    },
    maintenance: {
      title: 'Maintain Content Citability',
      what: 'Content is well-structured for AI citations.',
      why: '{citation}',
      how: ['Continue producing original, data-backed content'],
      measures: ['Ongoing AI citation tracking'],
    },
  },

  'passage-structure': {
    critical: {
      title: 'Improve Passage Structure',
      what: 'Content lacks clear passage structure for search engine passage ranking.',
      why: '{citation}',
      how: [
        'Break content into clear, self-contained sections',
        'Use descriptive subheadings (H2/H3) for each passage',
        'Ensure each section can stand alone as a useful answer',
        'Keep paragraphs focused on a single topic',
      ],
      measures: ['Check if passages appear in featured snippets', 'Monitor passage-level ranking'],
    },
    improvement: {
      title: 'Enhance Passage Structure',
      what: 'Some content has good structure but inconsistently: {detail}',
      why: '{citation}',
      how: ['Apply consistent passage structure to all content pages', 'Break up long unstructured blocks'],
      measures: ['Content structure audit'],
    },
    optimization: {
      title: 'Optimize Passage Formatting',
      what: 'Good passage structure. Fine-tune for better featured snippet eligibility.',
      why: '{citation}',
      how: ['Add summary sentences at the start of key sections', 'Use lists and tables for structured data'],
      measures: ['Featured snippet tracking'],
    },
    maintenance: {
      title: 'Maintain Passage Structure',
      what: 'Content passages are well-structured.',
      why: '{citation}',
      how: ['Include passage structure guidelines in content style guide'],
      measures: ['Periodic structure review'],
    },
  },

  'semantic-html': {
    critical: {
      title: 'Implement Semantic HTML',
      what: 'Pages use generic div/span elements instead of semantic HTML5 elements.',
      why: '{citation}',
      how: [
        'Replace div-based layouts with semantic elements: header, nav, main, article, section, aside, footer',
        'Use appropriate elements: time for dates, address for contact info, figure for media',
        'Ensure ARIA landmarks match semantic structure',
      ],
      measures: ['HTML validation audit', 'Accessibility score improvement'],
    },
    improvement: {
      title: 'Expand Semantic HTML Usage',
      what: 'Partial semantic HTML implementation: {detail}',
      why: '{citation}',
      how: ['Convert remaining generic elements to semantic HTML', 'Add missing landmark elements'],
      measures: ['Semantic element coverage audit'],
    },
    optimization: {
      title: 'Refine Semantic Markup',
      what: 'Good semantic HTML usage. Minor improvements possible.',
      why: '{citation}',
      how: ['Add microdata or RDFa where JSON-LD is not applicable'],
      measures: ['Accessibility and machine-readability audit'],
    },
    maintenance: {
      title: 'Maintain Semantic HTML',
      what: 'Semantic HTML is properly implemented.',
      why: '{citation}',
      how: ['Enforce semantic HTML standards in code reviews'],
      measures: ['Include in development guidelines'],
    },
  },

  'entity-clarity': {
    critical: {
      title: 'Establish Entity Clarity',
      what: 'Your brand entity is not clearly defined for search engines and AI systems.',
      why: '{citation}',
      how: [
        'Create a clear "About" page with definitive brand statements',
        'Use consistent brand name, description, and terminology everywhere',
        'Add Organization schema with sameAs links to authoritative profiles',
        'Build entity associations through consistent NAP (Name, Address, Phone)',
      ],
      measures: ['Check Google Knowledge Panel status', 'Test AI chatbot brand accuracy'],
    },
    improvement: {
      title: 'Strengthen Entity Clarity',
      what: 'Brand entity is partially defined: {detail}',
      why: '{citation}',
      how: ['Unify brand messaging across all pages', 'Link all social profiles in schema'],
      measures: ['Brand consistency audit'],
    },
    optimization: {
      title: 'Enhance Entity Recognition',
      what: 'Entity clarity is good. Strengthen external entity signals.',
      why: '{citation}',
      how: ['Get listed on authoritative directories', 'Seek Wikipedia or Wikidata presence'],
      measures: ['Knowledge Graph entry monitoring'],
    },
    maintenance: {
      title: 'Maintain Entity Clarity',
      what: 'Brand entity is well-defined and consistent.',
      why: '{citation}',
      how: ['Update entity information when brand details change'],
      measures: ['Quarterly brand consistency review'],
    },
  },

  'question-coverage': {
    critical: {
      title: 'Add Question-Based Content',
      what: 'Your site does not address common questions your audience is asking.',
      why: '{citation}',
      how: [
        'Research common questions in your industry using tools like AnswerThePublic',
        'Create FAQ pages addressing top questions',
        'Write blog posts that directly answer specific questions',
        'Use question-format headings (H2/H3)',
      ],
      measures: ['Track featured snippet wins for question queries', 'Monitor question-based organic traffic'],
    },
    improvement: {
      title: 'Expand Question Coverage',
      what: 'Some questions are addressed but gaps remain: {detail}',
      why: '{citation}',
      how: ['Identify unanswered questions from search data', 'Add FAQ sections to key pages'],
      measures: ['Question coverage gap analysis'],
    },
    optimization: {
      title: 'Optimize Question Content',
      what: 'Good question coverage. Optimize existing Q&A for featured snippets.',
      why: '{citation}',
      how: ['Format answers in featured-snippet-friendly patterns', 'Add FAQ schema to question content'],
      measures: ['Featured snippet and AI citation tracking'],
    },
    maintenance: {
      title: 'Maintain Question Coverage',
      what: 'Question coverage is comprehensive.',
      why: '{citation}',
      how: ['Monitor emerging questions in your industry', 'Update answers as information changes'],
      measures: ['Quarterly question trend review'],
    },
  },
};

/**
 * Get a template for a check ID and severity level.
 * Falls back to 'improvement' if the exact severity is not found.
 */
export function getTemplate(checkId, severity) {
  const checkTemplates = TEMPLATES[checkId];
  if (!checkTemplates) return null;
  return checkTemplates[severity] || checkTemplates.improvement || null;
}
