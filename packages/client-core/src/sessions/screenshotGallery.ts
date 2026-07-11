// Pure view-state helpers for the Cockpit screenshots gallery (issue #1254).
//
// A gallery thumbnail loads its bytes from the owning Director's disk through the Gateway proxy. The
// file can disappear between the moment the list was fetched and the moment the browser requests the
// image - someone deleted it, or a delete elsewhere raced this view. When that happens the <img>
// element fires its onError event and, left alone, the browser paints its built-in broken-image glyph
// with no explanation. The panel instead tracks which thumbnails failed to load and renders a small
// "image unavailable" placeholder in their place.
//
// The tracking lives here, in client-core, so it is pure and immutable: the decision is unit-tested
// without a browser, and the React panel (apps/cockpit/src/sessions/ScreenshotsPanel.tsx) stays thin.

/** The set of screenshot file names whose thumbnail failed to load. */
export type BrokenImageSet = ReadonlySet<string>;

/** The starting state: nothing is known to be broken. A fresh list load resets back to this. */
export const NO_BROKEN_IMAGES: BrokenImageSet = new Set<string>();

/**
 * Record that a thumbnail failed to load. Returns a NEW set - the input is never mutated, so the
 * shared NO_BROKEN_IMAGES constant and any prior React state can never be corrupted. When the file
 * name is already known to be broken the SAME set instance is returned, so a repeated onError does
 * not force a needless re-render.
 */
export function markImageBroken(current: BrokenImageSet, fileName: string): BrokenImageSet {
  if (current.has(fileName)) return current;
  const next = new Set(current);
  next.add(fileName);
  return next;
}

/** Whether a thumbnail is known to be broken and should render the placeholder instead of the <img>. */
export function isImageBroken(current: BrokenImageSet, fileName: string): boolean {
  return current.has(fileName);
}
