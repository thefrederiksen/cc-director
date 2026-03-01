/**
 * On-Page SEO recommendation generator.
 * Covers: title-tags, meta-descriptions, heading-hierarchy, image-alt-text,
 *         internal-linking, content-length, duplicate-content, open-graph
 */

import { generateCategoryRecs } from './base.mjs';

export function generateOnPageSeoRecs(audit) {
  return generateCategoryRecs('on-page-seo', audit, audit.hostname);
}
