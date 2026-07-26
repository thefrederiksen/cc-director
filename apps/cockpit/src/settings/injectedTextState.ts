// The pure logic behind the Injected text page, split out so it can be unit-tested without a browser or
// the Gateway (the repo convention: a *.ts of pure functions beside a *.test.ts, the view stays thin).
//
// validateTemplate is kept in STEP with the C# FleetPreambleRenderer.Validate: the page warns about an
// unrenderable template before a save the Gateway would reject anyway, so the failure lands on the person
// editing rather than as a surprising server error. The Gateway remains the authority - this is courtesy,
// not the gate.

export const IF_SIGNED_IN = "[IF_SIGNED_IN]";
export const END_IF = "[END_IF]";

/**
 * Check a template's [IF_SIGNED_IN]/[END_IF] markers are balanced and not nested. Returns null when the
 * template is fine, or a plain-English description of the problem. Mirrors the C# validator line-for-line
 * so the two never disagree about the same text.
 */
export function validateTemplate(template: string): string | null {
  let depth = 0;
  let lineNumber = 0;

  for (const raw of template.split("\n")) {
    lineNumber++;
    const trimmed = (raw.endsWith("\r") ? raw.slice(0, -1) : raw).trim();

    if (trimmed === IF_SIGNED_IN) {
      if (depth > 0) {
        return `Line ${lineNumber} opens another ${IF_SIGNED_IN} before the previous one was closed with ${END_IF}. These blocks cannot be nested.`;
      }
      depth++;
    } else if (trimmed === END_IF) {
      if (depth === 0) {
        return `Line ${lineNumber} has an ${END_IF} with no matching ${IF_SIGNED_IN} above it.`;
      }
      depth--;
    }
  }

  return depth > 0
    ? `An ${IF_SIGNED_IN} block was never closed with ${END_IF}.`
    : null;
}

/**
 * The self-harm case: a custom text with the fleet commands stripped out leaves the user's agents unable
 * to reach the rest of the fleet. That is their right, but it should be shown here rather than discovered
 * later. Injecting nothing at all is its own, clearer state, so an empty text gets no warning.
 */
export function fleetCommandsWarning(text: string): string | null {
  if (text.trim().length === 0) return null;
  return text.includes("cc-devthrottle")
    ? null
    : "Your text does not mention the fleet commands (cc-devthrottle ...), so agents started with it will not know how to reach the other sessions in your fleet.";
}

export type BannerTone = "ours" | "yours" | "editing";

export interface Banner {
  tone: BannerTone;
  title: string;
  detail: string;
}

/**
 * Whose text is live, as the one banner the user must never be wrong about. `editingUnsaved` wins: while
 * composing a not-yet-saved version, the banner says so, because nothing has changed for the agents yet.
 */
export function bannerFor(useYours: boolean, editingUnsaved: boolean): Banner {
  if (editingUnsaved) {
    return {
      tone: "editing",
      title: "Editing your own text - not saved yet",
      detail: "Nothing changes for your agents until you save. Save to make this the text they receive.",
    };
  }
  if (useYours) {
    return {
      tone: "yours",
      title: "Your agents receive YOUR text",
      detail: "You are running your own version. It does not receive DevThrottle's updates - that is the trade you made.",
    };
  }
  return {
    tone: "ours",
    title: "Your agents receive the DevThrottle text",
    detail: "This is the text DevThrottle ships. You can read it below and replace it with your own.",
  };
}
