import { describe, expect, it } from "vitest";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import { directorSessionRow } from "./DirectorDetailView";

// Defect 7: the Director-detail Sessions table carried THREE authorities in ONE row - a Gateway-stamped
// dot, a State cell re-derived from raw activity fields, and a SNOOZED tag read off the raw onHold
// boolean. These tests pin the row to the single fold. Each one was watched failing against the old
// code with the reported symptom before this file was kept; see the report for the evidence.
function session(fields: Partial<SessionDto> = {}): SessionDto {
  return {
    sessionId: "s1",
    createdAt: "2026-07-08T00:00:00Z",
    sortOrder: 0,
    ...fields,
  } as unknown as SessionDto;
}

describe("the Director-detail row renders the Gateway's fold and nothing else", () => {
  it("takes the State cell from the stamped label, not the raw activity state", () => {
    // THE DEFECT: this cell was humanizeState(s.assessedState ?? s.activityState). The Gateway folds a
    // parked session to "Snoozed", but the raw activity state underneath it still says WaitingForInput -
    // so the dot said parked-grey while the cell beside it said "Waiting for input". Same row, two
    // authorities, two answers.
    const s = session({
      effectiveColor: "grey",
      stateLabel: "Snoozed",
      triageBucket: "onHold",
      assessedState: "WaitingForInput",
      activityState: "WaitingForInput",
      onHold: true,
    } as Partial<SessionDto>);

    expect(directorSessionRow(s).state).toBe("Snoozed");
  });

  it("does not label a WORKING session SNOOZED just because the raw onHold flag is set", () => {
    // THE DEFECT, and the reason this mission exists: the tag was `s.onHold && <span>SNOOZED</span>`.
    // A snoozed session that WAKES UP and starts working arrives stamped blue - the fold applies the
    // working check at the top of its ladder - but it still carries onHold until the hold is cleared.
    // So the row drew a BLUE dot beside the word SNOOZED. The law: if a session is working, it is
    // blue, and nothing outranks working.
    const woken = session({
      effectiveColor: "blue",
      stateLabel: "Working",
      triageBucket: "active",
      onHold: true,
    } as Partial<SessionDto>);

    const row = directorSessionRow(woken);
    expect(row.dot).toBe("#3B82F6"); // blue - working
    expect(row.snoozed).toBe(false); // ...so no SNOOZED tag beside it
    expect(row.state).toBe("Working");
  });

  it("still badges a genuinely parked session", () => {
    // The fix must not simply delete the badge: a session the fold calls Snoozed still says so.
    // Lifecycle travels on a badge - it just comes from the fold rather than a raw flag.
    const parked = session({
      effectiveColor: "grey",
      stateLabel: "Snoozed",
      triageBucket: "onHold",
      onHold: true,
    } as Partial<SessionDto>);

    expect(directorSessionRow(parked).snoozed).toBe(true);
  });

  it("keeps the dot and the State cell answering from the same fold", () => {
    const s = session({
      effectiveColor: "red",
      stateLabel: "Needs you",
      triageBucket: "needsYou",
      activityState: "Working", // a stale raw field the row must ignore
    } as Partial<SessionDto>);

    const row = directorSessionRow(s);
    expect(row.dot).toBe("#EF4444");
    expect(row.state).toBe("Needs you");
  });

  it("reads the wingman sub-line from the fold's own word", () => {
    expect(directorSessionRow(session({
      effectiveColor: "yellow",
      stateLabel: "Wingman reading",
    } as Partial<SessionDto>)).briefing).toBe(true);

    expect(directorSessionRow(session({
      effectiveColor: "blue",
      stateLabel: "Working",
    } as Partial<SessionDto>)).briefing).toBe(false);
  });

  it("fails loudly when the Gateway did not stamp the row", () => {
    // The rule this module extends: a client that cannot get a stamped answer fails rather than
    // guessing. No fallback to raw fields - that is what produced the contradictions above.
    expect(() => directorSessionRow(session({ activityState: "Working" } as Partial<SessionDto>)))
      .toThrow("Gateway /sessions missing");
  });
});
