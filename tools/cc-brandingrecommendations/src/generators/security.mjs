/**
 * Security recommendation generator.
 * Covers: hsts, csp, x-content-type-options, x-frame-options,
 *         referrer-policy, permissions-policy
 */

import { generateCategoryRecs } from './base.mjs';

export function generateSecurityRecs(audit) {
  return generateCategoryRecs('security', audit, audit.hostname);
}
