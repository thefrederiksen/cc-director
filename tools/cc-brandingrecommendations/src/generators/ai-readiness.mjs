/**
 * AI Readiness recommendation generator.
 * Covers: llms-txt, ai-crawler-access, content-citability, passage-structure,
 *         semantic-html, entity-clarity, question-coverage
 */

import { generateCategoryRecs } from './base.mjs';

export function generateAiReadinessRecs(audit) {
  return generateCategoryRecs('ai-readiness', audit, audit.hostname);
}
