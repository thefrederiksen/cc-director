// Server-confirm-then-apply sequencing for the prompt queue (issue #1252).
//
// The prompt queue must never lose user text when the server call fails. The rule is: run the server
// verb first, and apply the local side effect (paste-into-composer for Pop, tear-down-the-edit-box for
// Save) ONLY after the verb has confirmed success. The old code applied the side effect first and then
// called the server, so a failed call left the item both pasted into the composer AND still queued
// (Pop), or discarded the typed edit while only showing an error banner (Save).

/**
 * Run a server verb, then apply a local side effect only when the verb succeeded.
 *
 * `runVerb` must resolve to true on success and false on failure (it reports its own errors and never
 * throws). `applyOnSuccess` runs after, and only if, the verb resolved true. Returns the verb's result
 * so callers can branch further if they need to.
 */
export async function confirmThenApply(
  runVerb: () => Promise<boolean>,
  applyOnSuccess: () => void,
): Promise<boolean> {
  const ok = await runVerb();
  if (ok) applyOnSuccess();
  return ok;
}
