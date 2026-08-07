// The Mission RECORDS on the Gateway - the first-class objects sessions attach to (see
// docs/new_architecture/mission-as-first-class-unit-of-work.md). This is what makes a mission exist on a
// screen INDEPENDENTLY of whether any session happens to be attached to it right now: a mission the owner
// created and has not staffed yet is still a mission, and it has to be visible or there is nowhere to drop
// a session onto.
//
// Before this client existed the Cockpit never read these records at all - it inferred "missions" by
// pattern-matching session NAMES, so a real mission with no name-matching session simply did not appear,
// and eleven live missions rendered as two. The records are the truth; the roster says who is on them.
import { authHeaders, gatewayFetch, GatewayError } from "../api/client";

/**
 * One Mission as the Gateway serves it (GET /missions). Hand-written rather than taken from `schema.ts`
 * for the same reason as `ModelDisplay` in the api client: the committed generated schema declares the
 * /missions responses as `never`, so there is no generated type to import. Delete this in favor of the
 * generated one when the schema is next regenerated properly - the two agree.
 */
export interface MissionDto {
  /** Stable identity of the Mission. This is the value sessions attach by, and the grouping identity. */
  missionId: string;
  /** Human-friendly name of the Mission. */
  missionName: string;
  /** The parent Mission this one nests under, or null for a root Mission. */
  parentMissionId?: string | null;
}

/**
 * Every Mission this account owns, newest ordering left to the Gateway. Tenant-scoped server-side from the
 * authenticated device key - the client neither sends nor can influence which account's missions it gets.
 *
 * Throws GatewayError on a non-2xx so the caller decides what to show. There is deliberately NO fallback to
 * an empty list here: a failed read means "we do not know what missions exist", which is a different thing
 * from "there are none", and a caller that cannot tell them apart will render the second while the first is
 * true.
 */
export async function listMissions(signal?: AbortSignal): Promise<MissionDto[]> {
  const res = await gatewayFetch("/missions", {
    method: "GET",
    headers: { Accept: "application/json" as const, ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await GatewayError.from(res, "load the mission list");

  // A 200 carrying something that is not an array of missions is a BROKEN response, not an empty fleet of
  // missions, and the two must not arrive at the same screen looking alike. Coercing the malformed case to
  // `[]` would render "no missions" over a Gateway that is answering wrongly - the caller would show a
  // confident, complete-looking, false answer. Throw instead and let the board say it does not know.
  const body: unknown = await res.json();
  if (!Array.isArray(body)) {
    throw new GatewayError(res.status, "The mission list came back in a shape this app cannot read.");
  }
  for (const m of body) {
    const id = (m as MissionDto | null)?.missionId;
    if (typeof id !== "string" || id.trim().length === 0) {
      throw new GatewayError(res.status, "The mission list contains a mission with no id.");
    }
  }
  return body as MissionDto[];
}
