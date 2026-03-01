/**
 * Backlink / PR Opportunities recommendation generator (cross-cutting, templated).
 * Produces backlink acquisition recommendations influenced by audit data.
 * Phase 1: Template-based only (no actual backlink checking).
 */

/**
 * Generate backlink opportunity recommendations.
 */
export function generateBacklinkRecs(audit, context) {
  const recs = [];
  const hostname = audit.hostname;

  // Check relevant audit signals
  const hasGoodContent = checkCategoryScore(audit, 'on-page-seo', 70);
  const hasStructuredData = checkCategoryScore(audit, 'structured-data', 60);

  // 1. Digital PR / linkable assets
  recs.push({
    id: 'backlinks-linkable-assets',
    title: 'Create Linkable Assets',
    category: 'backlinks',
    priority: null,
    impact: 4,
    effort: 4,
    what: 'Create high-value content that naturally attracts backlinks to ' + hostname + '.',
    why: 'Backlinks remain one of the top 3 ranking factors. Original data, tools, and comprehensive guides earn 3-5x more natural links than standard content. [Ahrefs, 2024]',
    how: [
      'Create original research or industry surveys with unique data',
      'Build free tools or calculators relevant to your audience' + (context.industry ? ' in ' + context.industry : ''),
      'Publish comprehensive "ultimate guides" on key topics' + (context.keywords ? ' including ' + context.keywords : ''),
      'Design shareable infographics with original data',
    ],
    measures: ['Number of referring domains acquired', 'Domain authority trend over time'],
    citation: 'Ahrefs (2024)',
    sourceCheck: 'backlinks',
    sourceStatus: 'GENERATED',
  });

  // 2. Guest posting / contributor outreach
  recs.push({
    id: 'backlinks-guest-posting',
    title: 'Launch Guest Posting Program',
    category: 'backlinks',
    priority: null,
    impact: 3,
    effort: 3,
    what: 'Contribute expert content to industry publications for backlinks and brand visibility.',
    why: 'Guest posting on relevant, authoritative sites builds backlinks and establishes thought leadership. Quality guest posts drive referral traffic and brand awareness.',
    how: [
      'Identify 10-20 industry publications that accept guest posts' + (context.industry ? ' in ' + context.industry : ''),
      'Pitch unique, valuable article ideas (not promotional content)',
      'Include a natural link back to relevant content on ' + hostname,
      'Build ongoing contributor relationships with top publications',
    ],
    measures: ['Guest posts published per month', 'Referral traffic from guest posts'],
    citation: 'Moz (2024)',
    sourceCheck: 'backlinks',
    sourceStatus: 'GENERATED',
  });

  // 3. Local/industry directory listings
  recs.push({
    id: 'backlinks-directories',
    title: 'Claim Industry Directory Listings',
    category: 'backlinks',
    priority: null,
    impact: 3,
    effort: 1,
    what: 'Register ' + hostname + ' in relevant industry directories and business listings.',
    why: 'Directory listings provide foundational backlinks and improve local/industry visibility. Consistent NAP (Name, Address, Phone) across directories strengthens entity signals.',
    how: [
      'Claim Google Business Profile if applicable',
      'Register on industry-specific directories' + (context.industry ? ' for ' + context.industry : ''),
      'Submit to general business directories (BBB, Crunchbase, etc.)',
      'Ensure consistent business information across all listings',
    ],
    measures: ['Number of directory listings', 'NAP consistency score'],
    citation: 'BrightLocal (2024)',
    sourceCheck: 'backlinks',
    sourceStatus: 'GENERATED',
  });

  // 4. Competitor backlink analysis (if competitors provided)
  if (context.competitors) {
    recs.push({
      id: 'backlinks-competitor-analysis',
      title: 'Analyze Competitor Backlinks',
      category: 'backlinks',
      priority: null,
      impact: 4,
      effort: 2,
      what: 'Study the backlink profiles of competitors (' + context.competitors + ') to find link opportunities.',
      why: 'Pages linking to your competitors are likely interested in your industry and may link to you too. Competitor backlink analysis is the fastest way to find proven link opportunities.',
      how: [
        'Use Ahrefs, Moz, or SEMrush to analyze competitor backlink profiles',
        'Identify sites linking to multiple competitors but not to ' + hostname,
        'Reach out with better content or a unique angle',
        'Replicate competitor link-building strategies that work',
      ],
      measures: ['Link opportunities identified', 'Outreach response rate', 'New backlinks acquired'],
      citation: 'Ahrefs (2024)',
      sourceCheck: 'backlinks',
      sourceStatus: 'GENERATED',
    });
  }

  // 5. Broken link building
  if (!hasGoodContent) {
    recs.push({
      id: 'backlinks-broken-link',
      title: 'Broken Link Building Outreach',
      category: 'backlinks',
      priority: null,
      impact: 3,
      effort: 3,
      what: 'Find broken links on other sites that could point to your content instead.',
      why: 'Broken link building has a 5-10% success rate, higher than cold outreach. You provide value by helping site owners fix broken links.',
      how: [
        'Find resource pages in your industry with broken outbound links',
        'Create or identify matching content on ' + hostname,
        'Email the site owner about the broken link and suggest your page as a replacement',
      ],
      measures: ['Outreach emails sent', 'Replacement links acquired'],
      citation: 'Backlinko (2024)',
      sourceCheck: 'backlinks',
      sourceStatus: 'GENERATED',
    });
  }

  return recs;
}

/**
 * Check if a category score meets a threshold.
 */
function checkCategoryScore(audit, categoryId, threshold) {
  const cat = audit.categories[categoryId];
  if (!cat) return false;
  return cat.score >= threshold;
}
