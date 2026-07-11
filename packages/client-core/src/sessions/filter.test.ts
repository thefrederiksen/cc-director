import { describe, expect, it } from "vitest";
import type { SessionDto } from "../api/client";
import {
  EMPTY_FILTER,
  applyFilter,
  filterIsActive,
  filterSummary,
  machineFacet,
  pruneFilter,
  repoFacet,
  sessionMatchesFilter,
  toggleValue,
} from "./filter";

function session(fields: Partial<SessionDto> & { sessionId?: string } = {}): SessionDto {
  return {
    sessionId: "s1",
    createdAt: "2026-07-08T00:00:00Z",
    sortOrder: 0,
    ...fields,
  } as unknown as SessionDto;
}

const roster = [
  session({ sessionId: "a", machineName: "SOREN_NORTH", repoPath: "D:/ReposFred/devthrottle" }),
  session({ sessionId: "b", machineName: "SOREN_NORTH", repoPath: "D:/ReposFred/mindzieWeb" }),
  session({ sessionId: "c", machineName: "SOREN_SOUTH", repoPath: "C:/repos/devthrottle" }),
  session({ sessionId: "d", machineName: "BUILD_BOX", repoPath: "C:/repos/devthrottle" }),
];

describe("filterIsActive", () => {
  it("is false for the empty filter", () => {
    expect(filterIsActive(EMPTY_FILTER)).toBe(false);
  });

  it("is true when a machine or repo is selected", () => {
    expect(filterIsActive({ machines: ["SOREN_NORTH"], repos: [] })).toBe(true);
    expect(filterIsActive({ machines: [], repos: ["devthrottle"] })).toBe(true);
  });
});

describe("applyFilter", () => {
  it("returns the roster unchanged when inactive", () => {
    expect(applyFilter(roster, EMPTY_FILTER)).toBe(roster);
  });

  it("filters by machine (union within the facet)", () => {
    const out = applyFilter(roster, { machines: ["SOREN_NORTH", "BUILD_BOX"], repos: [] });
    expect(out.map((s) => s.sessionId)).toEqual(["a", "b", "d"]);
  });

  it("filters by repo leaf", () => {
    const out = applyFilter(roster, { machines: [], repos: ["devthrottle"] });
    expect(out.map((s) => s.sessionId)).toEqual(["a", "c", "d"]);
  });

  it("combines machine AND repo", () => {
    const out = applyFilter(roster, { machines: ["SOREN_NORTH"], repos: ["devthrottle"] });
    expect(out.map((s) => s.sessionId)).toEqual(["a"]);
  });

  it("preserves the input ordering", () => {
    const reversed = [...roster].reverse();
    const out = applyFilter(reversed, { machines: [], repos: ["devthrottle"] });
    expect(out.map((s) => s.sessionId)).toEqual(["d", "c", "a"]);
  });
});

describe("sessionMatchesFilter", () => {
  it("treats an empty facet as no restriction", () => {
    expect(sessionMatchesFilter(roster[0], EMPTY_FILTER)).toBe(true);
  });
});

describe("facets", () => {
  it("counts machines, sorted by name", () => {
    expect(machineFacet(roster)).toEqual([
      { value: "BUILD_BOX", count: 1 },
      { value: "SOREN_NORTH", count: 2 },
      { value: "SOREN_SOUTH", count: 1 },
    ]);
  });

  it("counts repo leaves, sorted by name", () => {
    expect(repoFacet(roster)).toEqual([
      { value: "devthrottle", count: 3 },
      { value: "mindzieWeb", count: 1 },
    ]);
  });

  it("ignores sessions with no machine name", () => {
    expect(machineFacet([session({ machineName: "" }), session({ machineName: "X" })])).toEqual([
      { value: "X", count: 1 },
    ]);
  });
});

describe("filterSummary", () => {
  it("lists machines then repos", () => {
    expect(filterSummary({ machines: ["SOREN_NORTH"], repos: ["devthrottle"] })).toBe("SOREN_NORTH, devthrottle");
  });

  it("is empty for the empty filter", () => {
    expect(filterSummary(EMPTY_FILTER)).toBe("");
  });
});

describe("toggleValue", () => {
  it("adds a missing value and removes a present one", () => {
    expect(toggleValue([], "a")).toEqual(["a"]);
    expect(toggleValue(["a", "b"], "a")).toEqual(["b"]);
  });
});

describe("pruneFilter", () => {
  it("drops selections that no longer exist in the roster", () => {
    const pruned = pruneFilter({ machines: ["SOREN_NORTH", "GONE"], repos: ["devthrottle", "old-repo"] }, roster);
    expect(pruned).toEqual({ machines: ["SOREN_NORTH"], repos: ["devthrottle"] });
  });

  it("returns the same reference when nothing changed", () => {
    const f = { machines: ["SOREN_NORTH"], repos: [] };
    expect(pruneFilter(f, roster)).toBe(f);
  });
});
