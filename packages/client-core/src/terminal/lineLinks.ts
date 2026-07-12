// Local Files mission (Phase 2): turn ONE rendered terminal line into the clickable links on it, with
// their column positions, so an xterm link provider can underline each span and route a click. This is
// app-agnostic (no xterm, no UI imports) so it can be unit-tested on plain strings and reused by both
// terminal engines - the interactive cockpit terminal (interactive.ts) and, in Phase 3, the read-only
// mobile mirror (stream.ts).
//
// Detection reuses extractLinks() unchanged (the same detector chat uses): it yields the distinct
// URLs and absolute paths on the line, already trailing-punctuation- and line-number-stripped. Here we
// additionally locate WHERE each of those texts sits on the line (its 0-based column range) by scanning
// the raw line for the exact link text, so the provider can build an xterm buffer range. A link text
// that extractLinks rewrote away from its on-screen form (a file:// URL, which surfaces as the resolved
// local path) will not be found verbatim on the line and is simply skipped - no false underline.

import { extractLinks } from "../history/historyLinks";

/** One clickable link found on a single terminal line, with its 0-based half-open column range
 *  [start, end) into that line's text. */
export interface LineLink {
  text: string;
  isUrl: boolean;
  start: number;
  end: number;
}

/**
 * Find every clickable link on one rendered terminal line, with column positions. Absolute file paths
 * and http/https URLs only (whatever extractLinks detects); a link text that does not appear verbatim
 * on the line (e.g. a file:// URL rewritten to a local path) is skipped. Multiple occurrences of the
 * same text on the line are all returned so each is independently clickable.
 */
export function findLineLinks(lineText: string): LineLink[] {
  const out: LineLink[] = [];
  if (!lineText) return out;
  for (const link of extractLinks(lineText)) {
    if (link.text.length === 0) continue;
    let from = 0;
    // Locate every occurrence of this exact text so a path repeated on one line is clickable each time.
    for (;;) {
      const idx = lineText.indexOf(link.text, from);
      if (idx === -1) break;
      out.push({ text: link.text, isUrl: link.isUrl, start: idx, end: idx + link.text.length });
      from = idx + link.text.length;
    }
  }
  return out;
}
