/**
 * Technical SEO recommendation generator.
 * Covers: robots-txt, xml-sitemap, canonicals, https, redirect-chains,
 *         status-codes, crawl-depth, url-structure
 */

import { generateCategoryRecs } from './base.mjs';

export function generateTechnicalSeoRecs(audit) {
  return generateCategoryRecs('technical-seo', audit, audit.hostname);
}
