// The Assistant screen's transcript model (fleet assistant build): the pure, framework-free shape of
// the on-screen conversation and the operations the hook applies to it. Pulled out of the hook so the
// transcript rules are unit-tested directly - the hook stays a thin stateful shell.
//
// The transcript is DISPLAY ONLY. The real conversation context lives server-side keyed by this
// device (CarModeConversationStore), so the page sending nothing but the new turn is correct - this
// list is what the owner sees, not what the model sees.

import type { BrainAction } from "../fleetbrain/brainApi";

/** One entry in the on-screen conversation. `error` entries are failures shown in place (a failed
 *  turn stays visible in context rather than vanishing into a toast). */
export interface AssistantEntry {
  role: "user" | "assistant" | "error";
  text: string;
  /** The fleet actions the brain reports having taken this turn (assistant entries only). */
  actions?: BrainAction[];
  /** True when the brain is holding a destructive action and waits for a confirm/cancel next turn. */
  pendingConfirmation?: boolean;
}

/** How many entries the on-screen transcript retains. Oldest fall off; the server-side conversation
 *  context has its own, separate retention (last 16 messages) and is not affected by this. */
export const MAX_ENTRIES = 200;

/** Append one entry, dropping the oldest beyond MAX_ENTRIES. Pure - returns a new array. */
export function appendEntry(entries: readonly AssistantEntry[], entry: AssistantEntry): AssistantEntry[] {
  const next = [...entries, entry];
  return next.length > MAX_ENTRIES ? next.slice(next.length - MAX_ENTRIES) : next;
}

/** True when the latest assistant entry is holding a destructive action for confirmation, so the
 *  screen offers the explicit "Yes, do it" / "Cancel" buttons. Any entry after it clears the offer. */
export function awaitingConfirmation(entries: readonly AssistantEntry[]): boolean {
  const last = entries[entries.length - 1];
  return last?.role === "assistant" && last.pendingConfirmation === true;
}
