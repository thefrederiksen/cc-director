// Pure helpers for the Director settings editor's dirty tracking (issue #1255). The editor is a raw
// JSON text area; before the fix it had no notion of "unsaved edits", so Reload silently discarded them
// and Save was always enabled. These two helpers give the page the exact baseline it compares against:
// what the text looks like right after a load, and whether the current text differs from it.

/** Pretty-print raw settings JSON for editing. If the body is not valid JSON, keep it verbatim so the
 *  person can still see and fix it - this preserves the text, it does not hide a failure. The load
 *  baseline and the dirty check both use this so a freshly-loaded, untouched editor reads as clean. */
export function prettyPrintSettings(raw: string): string {
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}

/** True when the current text differs from the last-loaded (or last-saved) baseline. Compared exactly,
 *  including whitespace: a whitespace-only change is still an edit the person may want to keep, so it
 *  must enable Save and arm the discard-on-reload confirmation. */
export function isSettingsDirty(current: string, baseline: string): boolean {
  return current !== baseline;
}
