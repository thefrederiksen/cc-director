// The ONE transformation allowed on the user's dictated words on the client: joining transcript
// fragments (a paused-then-resumed segment onto the accumulated text, or the final live segment
// onto the accumulated paused text). Shared by the Speak dialog and the background Send pipeline so
// both join identically. Transcript integrity (CodingStyle.md s16): the server already returned the
// user's words corrected by the single dictionary-correction engine; this never rewrites the words,
// it only supplies exactly one separating space when neither side already carries the boundary.
export function joinText(left: string, right: string): string {
  if (!left) return right;
  if (!right) return left;
  const boundary = /\s$/.test(left) || /^\s/.test(right);
  return boundary ? left + right : left + " " + right;
}

// Insert `insert` into `existing` at index `caret`, adding exactly one separating space on a side
// only when the adjacent character is not already whitespace - so the inserted words never smush
// against the surrounding text. An out-of-range caret is clamped to the end; an empty insert returns
// `existing` unchanged. Mirrors the desktop DictationText.InsertAt so the Speak dialog drops dictation
// at the caret identically to the desktop Insert button.
export function insertAt(existing: string, caret: number, insert: string): string {
  if (!insert) return existing;
  if (caret < 0 || caret > existing.length) caret = existing.length;
  const prefix = existing.slice(0, caret);
  const suffix = existing.slice(caret);
  const needsSpaceBefore = prefix.length > 0 && !/\s$/.test(prefix);
  const needsSpaceAfter = suffix.length > 0 && !/^\s/.test(suffix);
  return prefix + (needsSpaceBefore ? " " : "") + insert + (needsSpaceAfter ? " " : "") + suffix;
}
