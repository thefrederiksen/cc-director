// Phase 1a of the Mission Screen (issue #1405): derive "missions" from the live fleet purely from the
// session NAME, with no backend change. The Cockpit's SessionDto (from the Gateway roster) does not
// carry a mission or role field, so for now a mission is read from the naming convention
// "<Mission> - <Role>" (dash, role second). Everything here is a pure function so the parser and the
// grouping are unit-tested on their own (see missionGrouping.test.ts); the view does no parsing.
//
// Two rules the Architect pinned before this was built (session 3457ddcf):
//   1. Strip ONE leading bracket tag first. Live names carry a move marker, e.g.
//      "[Moving Started] Gateway Cleanup - Manager" - without stripping the "[...]" the name would fall
//      to Standalone and we would lose the very mission we want grouped.
//   2. Match on the LAST " - <Role>". The mission is the text BEFORE that final " - <Role>", so a name
//      that itself contains " - " (e.g. "Foo - Bar - Manager") groups under "Foo - Bar".
// A trailing token that is NOT one of the known roles keeps the session Standalone (a bogus mission is
// worse than Standalone) - e.g. "mindzieWeb - remove Ask Mindzie" is Standalone, not a "mindzieWeb"
// mission.
import type { SessionDto } from "@devthrottle/client-core/api/client";

// The known roles a mission is staffed with. Only a session whose name ends in one of these (after the
// final " - ") is treated as part of a mission; anything else is Standalone. Case-insensitive match,
// canonical display casing.
const ROLE_DISPLAY: Record<string, string> = {
  architect: "Architect",
  manager: "Manager",
  worker: "Worker",
};

// The " - " that separates a mission name from its role in the naming convention.
const ROLE_SEPARATOR = " - ";

export interface ParsedMissionName {
  // The mission name exactly as written (bracket tag stripped, the text before the final " - Role").
  mission: string;
  // The role in canonical casing ("Architect" | "Manager" | "Worker").
  role: string;
}

// Strip one leading bracket tag from a session name: "[Moving Started] Gateway Cleanup - Manager" ->
// "Gateway Cleanup - Manager". Only a single leading "[...]" group (and the whitespace after it) is
// removed; brackets elsewhere in the name are left alone.
export function stripBracketTag(name: string): string {
  return name.replace(/^\s*\[[^\]]*\]\s*/, "").trim();
}

// Parse a raw session name into its mission + role, or null when the name is not a mission member. The
// name is first trimmed of a leading bracket tag, then split on its LAST " - "; the trailing token must
// be a known role for it to count as a mission.
export function parseMissionName(rawName: string | null | undefined): ParsedMissionName | null {
  const stripped = stripBracketTag((rawName ?? "").trim());
  if (stripped.length === 0) return null;

  const lastSep = stripped.lastIndexOf(ROLE_SEPARATOR);
  if (lastSep < 0) return null;

  const rolePart = stripped.slice(lastSep + ROLE_SEPARATOR.length).trim();
  const roleDisplay = ROLE_DISPLAY[rolePart.toLowerCase()];
  if (roleDisplay === undefined) return null;

  const mission = stripped.slice(0, lastSep).trim();
  if (mission.length === 0) return null;

  return { mission, role: roleDisplay };
}

// One session inside a mission card, carrying the role parsed from its name.
export interface MissionMember {
  session: SessionDto;
  role: string;
}

// One mission: a display name (as first seen) and its member sessions, ordered role-first.
export interface MissionGroup {
  // Lowercased mission name - the grouping identity, case-insensitive.
  key: string;
  // The mission name as first seen in the fleet (display casing).
  name: string;
  members: MissionMember[];
}

// The whole fleet split into missions (alphabetical) plus the Standalone sessions (rendered last as
// their own group).
export interface GroupedFleet {
  missions: MissionGroup[];
  standalone: SessionDto[];
}

// Order roles Architect -> Manager -> Worker within a mission card, so the lead reads first; an
// unknown role (should not happen past the parser) sorts last.
const ROLE_RANK: Record<string, number> = { Architect: 0, Manager: 1, Worker: 2 };

function roleRank(role: string): number {
  const r = ROLE_RANK[role];
  return r === undefined ? 99 : r;
}

// Stable order by session number ascending (the identity the owner reads), numbers before the
// unnumbered, then session id - so a row never jumps when its color changes. Mirrors the Fleet Map's
// flat sort so the two surfaces agree.
function byNumber(a: SessionDto, b: SessionDto): number {
  const na = Number(a.number);
  const nb = Number(b.number);
  const aHas = Number.isFinite(na);
  const bHas = Number.isFinite(nb);
  if (aHas && bHas && na !== nb) return na - nb;
  if (aHas !== bHas) return aHas ? -1 : 1;
  return String(a.sessionId ?? "").localeCompare(String(b.sessionId ?? ""));
}

// Group the live fleet by mission. Missions come out alphabetical (case-insensitive) by display name;
// each mission's members are ordered role-first then by session number; Standalone sessions (no
// parseable mission role) come out in session-number order for the caller to render last.
export function groupByMission(sessions: SessionDto[]): GroupedFleet {
  const byKey = new Map<string, MissionGroup>();
  const standalone: SessionDto[] = [];

  for (const s of sessions) {
    const parsed = parseMissionName(s.name);
    if (parsed === null) {
      standalone.push(s);
      continue;
    }
    const key = parsed.mission.toLowerCase();
    let group = byKey.get(key);
    if (group === undefined) {
      group = { key, name: parsed.mission, members: [] };
      byKey.set(key, group);
    }
    group.members.push({ session: s, role: parsed.role });
  }

  const missions = [...byKey.values()].sort((a, b) =>
    a.name.toLowerCase().localeCompare(b.name.toLowerCase()),
  );
  for (const m of missions) {
    m.members.sort((a, b) => roleRank(a.role) - roleRank(b.role) || byNumber(a.session, b.session));
  }
  standalone.sort(byNumber);

  return { missions, standalone };
}
