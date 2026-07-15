import type { SessionDto } from "@devthrottle/client-core/api/client";

/**
 * Pure helpers for the Fleet Map's card rendering, kept out of FleetMapView.tsx so they can be unit
 * tested. The Cockpit's vitest run has no DOM environment, so a helper that lives inside the component
 * file is a helper that cannot be tested; anything with a rule worth stating belongs here.
 */

/** The agent label to show on a card's meta row, or null when the card must not show one. */
export function agentBadgeText(s: SessionDto, pivot: string): string | null {
  // The agent pivot's lane header already states the agent for every card in the lane; repeating it per
  // card is noise, and it was the reason the badge existed on the title row at all.
  if (pivot === "agent") return null;
  const agent = (s.agent ?? "").trim();
  // An unknown agent still renders "?" rather than vanishing: a card with no agent is a fact worth
  // seeing, and a silently absent badge reads as "this card is fine" (issue #1625).
  return agent.length === 0 ? "?" : agent;
}
