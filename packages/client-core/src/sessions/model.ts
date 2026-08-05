import type { SessionDto } from "../api/client";

/**
 * Reading the Gateway's folded "which model is this session running" verdict (issue
 * devthrottle_internal#1340) for the surfaces that show it: the Cockpit's Fleet Map cards, its session
 * roster, and its session view.
 *
 * There is deliberately NO wording in this file. Every string a user sees - the badge text, the tooltip,
 * and above all WHICH of the two absences applies - is computed once on the Gateway (the C#
 * ModelDisplayFold) and rides on `session.modelDisplay`. This module answers one question only: is there a
 * verdict to render? That split is the whole point. The absences mean opposite things - "this session has
 * not finished a turn yet, the model is coming" against "this agent can never report a model" - and a
 * client that writes its own words for them will eventually write the hopeful one for the hopeless case,
 * which is the defect the Voice screen already taught us (a "Generate narration now" button that could
 * never work).
 */

/** What a surface renders for a session's model: the Gateway's words, its tooltip, and whether to mute. */
export interface ModelChip {
  /** The badge text, verbatim from the Gateway. Never assembled here. */
  text: string;
  /** The tooltip, verbatim: the full recorded id, or the sentence naming the absence. */
  title: string;
  /** True in both absent states, so a surface can outline the chip instead of filling it. STYLING only. */
  absent: boolean;
}

/**
 * The chip for one session, or null when there is nothing to render.
 *
 * Null means the GATEWAY STAMPED NOTHING - an older Gateway, or a Director-local response - not "no
 * model". A missing model is a stamped verdict with words of its own; only a missing VERDICT is silent
 * here, because inventing a placeholder for it would be this client ruling in the Gateway's place.
 */
export function modelChipOf(session: SessionDto): ModelChip | null {
  const d = session.modelDisplay;
  if (d === null || d === undefined) return null;
  const text = (d.text ?? "").trim();
  if (text.length === 0) return null;
  return { text, title: (d.tooltip ?? "").trim(), absent: d.isAbsent === true };
}
