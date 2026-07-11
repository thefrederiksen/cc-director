// Appending text into the session composer (issues #972, #1266).
//
// The composer text is owned by SessionDetail, and several surfaces drop text into it: the queue's Pop,
// the screenshot gallery's Insert, and the Source Control tab's click-a-file-to-insert-its-path. They
// all share this one rule so the separating space is applied identically everywhere: keep exactly one
// space between the existing text and the appended text, and never prepend a leading space when the box
// is empty. Kept pure and side-effect-free so the Source Control insert behaviour is unit-testable.

/**
 * Append `insert` to `current`, separated by a single space unless `current` is empty or already ends
 * with a space. An empty `insert` leaves `current` unchanged.
 */
export function appendToCompose(current: string, insert: string): string {
  if (insert.length === 0) return current;
  return current.length > 0 && !current.endsWith(" ") ? `${current} ${insert}` : `${current}${insert}`;
}
