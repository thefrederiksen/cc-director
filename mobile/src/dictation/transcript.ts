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
