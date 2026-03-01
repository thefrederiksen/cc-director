/**
 * Social Presence recommendation generator (cross-cutting, templated).
 * Produces social media recommendations influenced by audit data.
 * Phase 1: Template-based only (no actual social media checking).
 */

/**
 * Generate social media presence recommendations.
 */
export function generateSocialPresenceRecs(audit, context) {
  const recs = [];
  const hostname = audit.hostname;

  // Check Open Graph status from on-page-seo
  const hasOgIssues = checkStatus(audit, 'on-page-seo', 'open-graph', ['FAIL', 'WARN']);
  const hasOrgSchema = checkStatus(audit, 'structured-data', 'organization-schema', ['PASS']);

  // 1. Social media profile optimization
  recs.push({
    id: 'social-presence-profiles',
    title: 'Optimize Social Media Profiles',
    category: 'social-presence',
    priority: null,
    impact: 3,
    effort: 2,
    what: 'Ensure all social media profiles for ' + hostname + ' are complete, consistent, and linked to your website.',
    why: 'Consistent social profiles strengthen your brand entity in search engines and AI systems. sameAs links in Organization schema connect your web presence. [Kalicube, 2025]',
    how: [
      'Audit all social profiles (LinkedIn, Twitter/X, Facebook, Instagram, YouTube)',
      'Ensure consistent name, logo, description, and URL across all profiles',
      'Add website link to all profile bios',
      hasOrgSchema
        ? 'Your Organization schema already exists -- verify it includes sameAs links to all social profiles'
        : 'Add Organization schema with sameAs links pointing to each social profile URL',
    ],
    measures: ['All profiles link to website', 'Organization schema includes all sameAs links'],
    citation: 'Kalicube (2025)',
    sourceCheck: 'social-presence',
    sourceStatus: 'GENERATED',
  });

  // 2. Social sharing optimization (tied to OG tags)
  if (hasOgIssues) {
    recs.push({
      id: 'social-presence-sharing',
      title: 'Fix Social Sharing Previews',
      category: 'social-presence',
      priority: null,
      impact: 3,
      effort: 1,
      what: 'Social media shares of ' + hostname + ' pages display poorly due to missing Open Graph tags.',
      why: 'Posts with proper Open Graph images and descriptions get 2-3x more engagement on social platforms. [Buffer, 2024]',
      how: [
        'Add og:title, og:description, og:image to all pages',
        'Create social-optimized images at 1200x630 pixels',
        'Add Twitter Card meta tags for Twitter/X optimization',
        'Test with Facebook Sharing Debugger and Twitter Card Validator',
      ],
      measures: ['All pages have complete OG tags', 'Social preview looks correct in debugging tools'],
      citation: 'Buffer (2024)',
      sourceCheck: 'social-presence',
      sourceStatus: 'GENERATED',
    });
  }

  // 3. Content distribution strategy
  recs.push({
    id: 'social-presence-distribution',
    title: 'Create Content Distribution Plan',
    category: 'social-presence',
    priority: null,
    impact: 3,
    effort: 2,
    what: 'Establish a systematic approach to distributing content across social channels.',
    why: 'Social signals drive referral traffic and help search engines discover new content faster. Consistent social promotion increases content reach by 5-10x.',
    how: [
      'Choose 2-3 primary social platforms where your audience is active' + (context.industry ? ' (key for ' + context.industry + ')' : ''),
      'Share every new blog post/page across selected platforms',
      'Repurpose long-form content into social-friendly formats (threads, carousels, short videos)',
      'Engage with industry conversations and relevant hashtags',
    ],
    measures: ['Social referral traffic in analytics', 'Social engagement metrics (shares, comments)'],
    citation: 'Sprout Social (2024)',
    sourceCheck: 'social-presence',
    sourceStatus: 'GENERATED',
  });

  return recs;
}

/**
 * Check if a specific check in a category has one of the given statuses.
 */
function checkStatus(audit, categoryId, checkId, statuses) {
  const cat = audit.categories[categoryId];
  if (!cat) return false;
  const check = cat.checks.find(c => c.id === checkId);
  if (!check) return false;
  return statuses.includes(check.status);
}
