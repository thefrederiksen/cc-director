/**
 * Structured Data recommendation generator.
 * Covers: json-ld-present, organization-schema, article-schema,
 *         faq-schema, breadcrumb-schema, schema-validity
 */

import { generateCategoryRecs } from './base.mjs';

export function generateStructuredDataRecs(audit) {
  return generateCategoryRecs('structured-data', audit, audit.hostname);
}
