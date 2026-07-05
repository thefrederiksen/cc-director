// TypeScript port of the Cockpit's BriefFallback (src/CcDirector.Cockpit/Services/BriefFallback.cs,
// itself a mirror of Core BriefBuilder.FallbackNeedsYou). The Brief has a degrade path for an OLD
// Director that serves only /summary (no structured brief): the reply's final non-empty paragraph is
// shown as the "needs you" block, verbatim by construction. This composition is pure text handling
// with no secret, so in the React Cockpit it runs in the browser (issue #970) exactly as the C# did.

/**
 * The final non-empty paragraph of a reply, or null when the reply is empty/whitespace. Paragraphs
 * are split on blank lines; if the chosen paragraph is longer than maxChars, its LAST maxChars are
 * kept (the tail carries the ask), matching the C# BriefFallback.FinalParagraph byte-for-byte.
 */
export function finalParagraph(
  reply: string | null | undefined,
  maxChars = 600,
): string | null {
  if (!reply || reply.trim().length === 0) return null;

  const paragraphs = reply
    .replace(/\r\n/g, "\n")
    .split("\n\n")
    .filter((p) => p.length > 0);

  for (let i = paragraphs.length - 1; i >= 0; i--) {
    const p = paragraphs[i].trim();
    if (p.length === 0) continue;
    return p.length <= maxChars ? p : p.slice(p.length - maxChars);
  }
  return null;
}
