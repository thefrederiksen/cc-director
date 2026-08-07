// Group the live fleet into MISSIONS, using the mission a session is actually ATTACHED to.
//
// A session's mission is `SessionDto.missionId` - the attachment link the Gateway stamps (at spawn, or
// later through `POST /sessions/{sid}/mission`, which is what `cc-devthrottle mission attach` drives). Its
// role is `SessionDto.sessionRole`, which the Gateway resolves and pushes. Both arrive on every session in
// the roster. Nothing here parses, infers, or re-derives either one.
//
// WHAT THIS REPLACED, so it is not reintroduced. Until now this module derived "missions" by pattern-
// matching the session NAME for "<Mission> - <Role>". That was Phase 1a of issue #1405, written when the
// roster genuinely carried no mission field, and explicitly temporary. The field arrived; the stand-in did
// not get removed. The result was a screen wired to the weakest of five different notions of "mission":
// eleven live missions rendered as two, and seven sessions attached to one mission showed up as one,
// because the other six were not NAMED the right way. Attaching a session could not move it, and renaming a
// session could.
//
// So: the name is DISPLAY ONLY. "<Mission> - <Role>" remains a good human naming habit - it reads well and
// sorts well - but it is never again load-bearing. If a session's mission is wrong on screen, the fix is the
// attachment, not the name.
//
// There is deliberately NO fallback to name parsing when a session has no `missionId`. A session with no
// mission is Standalone, which is a true and ordinary state. Guessing a mission from its name would be the
// same defect in a quieter form: the screen would show a mission that the fleet does not agree exists, and
// nothing the owner did to the attachment would change it.
import type { SessionDto } from "@devthrottle/client-core/api/client";
import type { MissionDto } from "@devthrottle/client-core/missions/missions";

// Canonical display casing for the roles the Gateway resolves. A role outside this set is still SHOWN (it
// is the Gateway's answer and the client does not overrule it), it just sorts last.
const ROLE_DISPLAY: Record<string, string> = {
  architect: "Architect",
  manager: "Manager",
  worker: "Worker",
};

// "Standalone" is the Gateway's way of saying "no role", not a role. It must not render as a badge - see
// FleetMapView, which suppresses it the same way on the other pivots.
const NOT_A_ROLE = "standalone";

// The role to display for a session, or null when it has none. Read from the Gateway's `sessionRole`,
// never computed.
export function displayRole(session: SessionDto): string | null {
  const raw = (session.sessionRole ?? "").trim();
  if (raw.length === 0 || raw.toLowerCase() === NOT_A_ROLE) return null;
  return ROLE_DISPLAY[raw.toLowerCase()] ?? raw;
}

// One session inside a mission card, with the role the Gateway gave it (null when it has none).
export interface MissionMember {
  session: SessionDto;
  role: string | null;
}

// One mission: its identity, its display name, and the sessions attached to it. A mission with no attached
// session is still a mission and still appears - `members` is simply empty.
export interface MissionGroup {
  /** The mission id - the grouping identity, the value the WHY is written against, and the value a future
   *  drag-and-drop would attach by. */
  key: string;
  /** The mission's display name. */
  name: string;
  /** The mission's WHY, straight off the record. Empty means UNSET and the card shows its loud flag. A
   *  mission known only from a session (see `fromSessionOnly`) has no record to read it from, so it is
   *  empty there - which is honest: we genuinely do not have it. */
  why: string;
  members: MissionMember[];
  /** True when this mission came only from an attached session, not from the mission list (see below). */
  fromSessionOnly: boolean;
}

// The whole fleet split into missions plus the Standalone sessions (rendered last as their own group).
export interface GroupedFleet {
  missions: MissionGroup[];
  standalone: SessionDto[];
}

/**
 * Split the missions into the ones with sessions on them and the ones without.
 *
 * EMPTY IS NOT THE SAME AS FINISHED, and this function only knows about the first. A mission is empty
 * because nobody is on it RIGHT NOW - which is equally true of a mission created ten seconds ago and of
 * one that shipped a week ago. Hiding the empties is therefore a VIEW preference and never a statement
 * that the work is over; ending a mission is a state on the record, and it is a different feature.
 *
 * The caller must show the hidden COUNT whenever it hides any. A card that disappears with no trace is
 * the same quiet-wrong-answer this screen was rebuilt to stop giving: the owner cannot tell "there are
 * none" from "you are not being shown them".
 */
export function splitEmptyMissions(missions: MissionGroup[]): {
  staffed: MissionGroup[];
  empty: MissionGroup[];
} {
  const staffed: MissionGroup[] = [];
  const empty: MissionGroup[] = [];
  for (const m of missions) (m.members.length > 0 ? staffed : empty).push(m);
  return { staffed, empty };
}

// Order roles Architect -> Manager -> Worker within a mission card, so the lead reads first. A role the
// Gateway sent that is not one of the three, and a session with no role at all, sort last.
const ROLE_RANK: Record<string, number> = { Architect: 0, Manager: 1, Worker: 2 };

function roleRank(role: string | null): number {
  if (role === null) return 98;
  const r = ROLE_RANK[role];
  return r === undefined ? 99 : r;
}

// Stable order by session number ascending (the identity the owner reads), numbers before the unnumbered,
// then session id - so a row never jumps when its color changes. Mirrors the Fleet Map's flat sort so the
// two surfaces agree.
function byNumber(a: SessionDto, b: SessionDto): number {
  const na = Number(a.number);
  const nb = Number(b.number);
  const aHas = Number.isFinite(na);
  const bHas = Number.isFinite(nb);
  if (aHas && bHas && na !== nb) return na - nb;
  if (aHas !== bHas) return aHas ? -1 : 1;
  return String(a.sessionId ?? "").localeCompare(String(b.sessionId ?? ""));
}

// A mission id in whatever casing it arrived in, folded so the roster and the mission list join reliably.
// Both sides are Gateway-minted GUIDs, so this only guards the casing, not a format difference.
function missionKey(id: string): string {
  return id.trim().toLowerCase();
}

/**
 * Group the live fleet by the mission each session is ATTACHED to.
 *
 * `missions` is the Gateway's mission list. Pass it so missions with no attached session still appear;
 * omit it (or pass an empty list) and the result contains only missions that at least one session is on -
 * which is what the caller should render if the mission list could not be loaded, because showing a
 * shorter list of real missions is honest, while showing none of them is not.
 *
 * A session attached to a mission the list does not contain still gets a card, built from the name cached
 * on the session and flagged `fromSessionOnly`. That case is real: the mission records and the workflow
 * runs are two different stores, and they are already observed to disagree. Dropping such a session to
 * Standalone would hide a genuine attachment - exactly the failure this module was rewritten to end.
 *
 * Missions come out alphabetical by display name (case-insensitive); each mission's members are ordered
 * role-first then by session number; Standalone comes out in session-number order for the caller to render
 * last.
 */
export function groupByMission(
  sessions: SessionDto[],
  missions: MissionDto[] = [],
): GroupedFleet {
  const byKey = new Map<string, MissionGroup>();

  // Seed from the mission RECORDS first, so an unstaffed mission is present and so the record's name wins
  // over the copy cached on a session (which is stamped at attach time and would be stale after a rename).
  for (const m of missions) {
    const id = (m.missionId ?? "").trim();
    if (id.length === 0) continue;
    byKey.set(missionKey(id), {
      key: id,
      name: (m.missionName ?? "").trim(),
      why: (m.why ?? "").trim(),
      members: [],
      fromSessionOnly: false,
    });
  }

  const standalone: SessionDto[] = [];

  for (const s of sessions) {
    const id = (s.missionId ?? "").trim();
    if (id.length === 0) {
      standalone.push(s);
      continue;
    }
    const key = missionKey(id);
    let group = byKey.get(key);
    if (group === undefined) {
      group = {
        key: id,
        name: (s.missionName ?? "").trim(),
        why: "",
        members: [],
        fromSessionOnly: true,
      };
      byKey.set(key, group);
    }
    group.members.push({ session: s, role: displayRole(s) });
  }

  // A mission whose name we know from neither the record nor the session still has to render as SOMETHING
  // the owner can click - never a blank card that looks like a rendering fault.
  for (const g of byKey.values()) {
    if (g.name.length === 0) g.name = "(unnamed mission)";
  }

  const missionGroups = [...byKey.values()].sort((a, b) =>
    a.name.toLowerCase().localeCompare(b.name.toLowerCase()),
  );
  for (const m of missionGroups) {
    m.members.sort((a, b) => roleRank(a.role) - roleRank(b.role) || byNumber(a.session, b.session));
  }
  standalone.sort(byNumber);

  return { missions: missionGroups, standalone };
}
