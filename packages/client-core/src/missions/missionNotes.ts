// Mission-WHY client for the Mission Screen (Phase 1b, issue #1405). Same-origin against the Gateway
// front door with the injected Bearer (authHeaders), exactly like the AI settings client. The WHY store
// is durable and shared on the Gateway, so every client (this Cockpit, the phone, the future
// Mission-Control chat) reads and writes the SAME WHY through these two calls - never per-browser
// storage. The mission is identified by the SAME normalized key the Cockpit's groupByMission derives;
// the server lower-cases the display name, so the client sends the mission's display name as written.
import { authHeaders, GatewayError } from "../api/client";

/** One mission's WHY as the Gateway stores it (GET /gateway/missions/notes). */
export interface MissionNote {
  /** The normalized (trimmed, lower-cased) mission key - matches groupByMission's key. */
  key: string;
  /** The mission display name as last written. */
  mission: string;
  /** The WHY text (never empty - an empty why is stored as "unset", i.e. no note). */
  why: string;
  /** When the WHY was last set (ISO-8601 UTC). */
  updatedAt: string;
}

/** All set WHYs. A mission with no note simply has no entry (the card shows its "no why set" flag). */
export async function getMissionNotes(): Promise<MissionNote[]> {
  const res = await fetch("/gateway/missions/notes", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
  });
  if (!res.ok) throw new GatewayError(res.status, `GET /gateway/missions/notes failed: ${res.status}`);
  const body = (await res.json()) as { notes?: MissionNote[] };
  return Array.isArray(body.notes) ? body.notes : [];
}

/**
 * Set (or clear) a mission's WHY. An empty or whitespace-only `why` UNSETS it (the card returns to its
 * flag). Returns the stored note, or null when it was cleared.
 */
export async function setMissionNote(mission: string, why: string): Promise<MissionNote | null> {
  const res = await fetch("/gateway/missions/notes", {
    method: "PUT",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ mission, why }),
  });
  if (!res.ok) {
    const err = (await res.json().catch(() => ({}))) as { error?: string };
    throw new GatewayError(res.status, err.error ?? `PUT /gateway/missions/notes failed: ${res.status}`);
  }
  const body = (await res.json()) as { note?: MissionNote | null };
  return body.note ?? null;
}
