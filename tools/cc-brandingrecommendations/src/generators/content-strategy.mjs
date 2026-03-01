/**
 * Content Strategy recommendation generator (cross-cutting).
 * Synthesizes across on-page-seo, ai-readiness, and structured-data
 * to produce content strategy recommendations.
 */

/**
 * Generate content strategy recommendations from audit data.
 */
export function generateContentStrategyRecs(audit, context) {
  const recs = [];
  const hostname = audit.hostname;

  // Analyze content-related check statuses across categories
  const contentChecks = gatherContentChecks(audit);

  // 1. Content calendar recommendation (always relevant)
  if (contentChecks.thinContent || contentChecks.lowQuestionCoverage) {
    recs.push({
      id: 'content-strategy-calendar',
      title: 'Create a Content Calendar',
      category: 'content-strategy',
      priority: null,
      impact: 4,
      effort: 2,
      what: 'Establish a regular content publishing schedule to address content gaps on ' + hostname + '.',
      why: 'Consistent publishing signals freshness to search engines and builds topical authority. Sites publishing weekly see 3.5x more traffic than those publishing monthly.',
      how: [
        'Audit existing content to identify gaps and opportunities',
        'Research target keywords' + (context.keywords ? ' including: ' + context.keywords : '') + ' for content ideas',
        'Plan 4-8 pieces of content per month covering key topics',
        'Include a mix of blog posts, guides, and FAQ content',
        'Assign publish dates and responsible owners',
      ],
      measures: ['Track content published vs. planned', 'Monitor organic traffic growth month-over-month'],
      citation: 'HubSpot (2024)',
      sourceCheck: 'content-strategy',
      sourceStatus: 'GENERATED',
    });
  }

  // 2. Topic cluster recommendation
  if (contentChecks.weakInternalLinking || contentChecks.thinContent) {
    recs.push({
      id: 'content-strategy-topic-clusters',
      title: 'Build Topic Clusters',
      category: 'content-strategy',
      priority: null,
      impact: 4,
      effort: 3,
      what: 'Organize content into topic clusters with pillar pages and supporting articles.',
      why: 'Topic clusters establish topical authority and improve internal linking structure. Sites using topic clusters see 20-30% organic traffic improvement.',
      how: [
        'Identify 3-5 core topics relevant to your business' + (context.industry ? ' in the ' + context.industry + ' industry' : ''),
        'Create comprehensive pillar pages (2000+ words) for each core topic',
        'Write 5-10 supporting articles per pillar topic',
        'Link all supporting articles back to the pillar page and vice versa',
      ],
      measures: ['Track pillar page rankings', 'Monitor internal link distribution'],
      citation: 'HubSpot (2024)',
      sourceCheck: 'content-strategy',
      sourceStatus: 'GENERATED',
    });
  }

  // 3. Content optimization recommendation
  if (contentChecks.weakTitles || contentChecks.weakDescriptions) {
    recs.push({
      id: 'content-strategy-optimization',
      title: 'Optimize Existing Content',
      category: 'content-strategy',
      priority: null,
      impact: 3,
      effort: 2,
      what: 'Update and optimize existing pages for better search performance.',
      why: 'Refreshing existing content is 2-3x more efficient than creating new content. Updated pages typically see 100%+ traffic increase.',
      how: [
        'Identify pages ranking on page 2-3 of search results (highest ROI for optimization)',
        'Update titles and meta descriptions with target keywords',
        'Expand thin content to 800+ words with valuable information',
        'Add internal links to and from high-authority pages',
      ],
      measures: ['Track ranking improvements for optimized pages', 'Monitor traffic changes before/after'],
      citation: 'Ahrefs (2024)',
      sourceCheck: 'content-strategy',
      sourceStatus: 'GENERATED',
    });
  }

  // 4. Brand voice/messaging recommendation (from entity clarity)
  if (contentChecks.weakEntityClarity) {
    recs.push({
      id: 'content-strategy-brand-voice',
      title: 'Define Brand Voice and Messaging',
      category: 'content-strategy',
      priority: null,
      impact: 3,
      effort: 2,
      what: 'Create consistent brand messaging guidelines for all content on ' + hostname + '.',
      why: 'Consistent brand voice improves entity recognition by search engines and AI, and builds trust with audiences.',
      how: [
        'Document your brand voice (tone, personality, key phrases)',
        'Create a messaging framework with key value propositions',
        'Write a clear, consistent "About" page and boilerplate',
        'Train content creators on brand guidelines',
      ],
      measures: ['Brand consistency audit across all pages', 'AI chatbot accuracy when asked about your brand'],
      citation: 'Kalicube (2025)',
      sourceCheck: 'content-strategy',
      sourceStatus: 'GENERATED',
    });
  }

  return recs;
}

/**
 * Gather content-related signals from audit checks.
 */
function gatherContentChecks(audit) {
  const result = {
    thinContent: false,
    lowQuestionCoverage: false,
    weakInternalLinking: false,
    weakTitles: false,
    weakDescriptions: false,
    weakEntityClarity: false,
  };

  const onPage = audit.categories['on-page-seo'];
  if (onPage) {
    for (const check of onPage.checks) {
      if (check.id === 'content-length' && (check.status === 'FAIL' || check.status === 'WARN')) {
        result.thinContent = true;
      }
      if (check.id === 'internal-linking' && (check.status === 'FAIL' || check.status === 'WARN')) {
        result.weakInternalLinking = true;
      }
      if (check.id === 'title-tags' && (check.status === 'FAIL' || check.status === 'WARN')) {
        result.weakTitles = true;
      }
      if (check.id === 'meta-descriptions' && (check.status === 'FAIL' || check.status === 'WARN')) {
        result.weakDescriptions = true;
      }
    }
  }

  const ai = audit.categories['ai-readiness'];
  if (ai) {
    for (const check of ai.checks) {
      if (check.id === 'question-coverage' && (check.status === 'FAIL' || check.status === 'WARN')) {
        result.lowQuestionCoverage = true;
      }
      if (check.id === 'entity-clarity' && (check.status === 'FAIL' || check.status === 'WARN')) {
        result.weakEntityClarity = true;
      }
    }
  }

  return result;
}
