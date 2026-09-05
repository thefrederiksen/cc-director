import { describe, it, expect } from "vitest";
import {
  summarizeThrottle,
  summarizeRepos,
  summarizeAgents,
  formatShare,
  last24HourKeys,
  windowSeries,
  emptyInputHour,
  localHourLabel,
  safeTimeZone,
  type ThrottleFigure,
  type RepoStat,
  type AgentStat,
  type InputHour,
} from "./statsClient";

// The pure "Your Throttle" summary math. The network read (getThrottle) is exercised through the app; here
// we lock the honest share arithmetic that both shells render over the figure the Gateway serves.
//
// There is no character volume anywhere in these shapes (mission "Clean up Your Throttle", ruling R16): the
// figure comes from the submission ledger, which counts turns and nothing else.

function figure(buckets: ThrottleFigure["buckets"]): ThrottleFigure {
  return {
    definition: "The shared figure is computed over activity_events rows where EventType is turn-submitted and InputOrigin is present, grouped by the origin's modality and surface.",
    unit: "submitted turns",
    window: { fromUtc: "2026-08-06T00:00:00Z", toUtc: "2026-09-05T00:00:00Z", isDefault: true, label: "Last 30 days" },
    ledger: { retentionDays: 30, earliestUtc: "2026-08-06T04:00:00Z" },
    turns: buckets.reduce((t, b) => t + b.turns, 0),
    voiceTurns: buckets.filter((b) => b.modality === "voice").reduce((t, b) => t + b.turns, 0),
    typedTurns: buckets.filter((b) => b.modality === "typed").reduce((t, b) => t + b.turns, 0),
    sessions: 1,
    buckets,
    hourlyTurns: [],
    agents: [],
    repos: [],
    reposUnattributedTurns: 0,
    excluded: { noInputOrigin: 0, agentDriven: 0, framework: 0, unresolved: 0 },
    agentDrivenTurns: 0,
  };
}

function repo(repoName: string, turns: number, voiceTurns: number, sessions: number): RepoStat {
  return { repo: `owner/${repoName}`, repoName, turns, voiceTurns, typedTurns: turns - voiceTurns, sessions, checkouts: [`D:/${repoName}`] };
}

function agent(agentToken: string, agentName: string, turns: number, voiceTurns: number, sessions: number,
               agentDrivenTurns = 0): AgentStat {
  return { agent: agentToken, agentName, turns, voiceTurns, typedTurns: turns - voiceTurns, sessions, agentDrivenTurns };
}

describe("summarizeThrottle", () => {
  it("computes turn totals and shares across modality and surface", () => {
    const s = summarizeThrottle(
      figure([
        { modality: "voice", surface: "phone", turns: 3 },
        { modality: "typed", surface: "desktop", turns: 1 },
      ]),
    );
    expect(s.totalTurns).toBe(4);
    expect(s.voiceTurns).toBe(3);
    expect(s.typedTurns).toBe(1);
    expect(s.turnsBySurface.phone).toBe(3);
    expect(s.turnsBySurface.desktop).toBe(1);
    expect(s.voiceShare).toBeCloseTo(0.75);
    expect(s.phoneShare).toBeCloseTo(0.75);
    expect(s.hasData).toBe(true);
  });

  it("reports null shares (not 0%) when no turns are counted yet", () => {
    const s = summarizeThrottle(figure([]));
    expect(s.totalTurns).toBe(0);
    expect(s.voiceShare).toBeNull();
    expect(s.phoneShare).toBeNull();
    expect(s.hasData).toBe(false);
  });

  // The unknown surface is a recorded answer ("typed, somewhere we could not name"), kept as its own bucket
  // rather than folded into a real surface - and it is still a counted turn in the voice share.
  it("keeps an unknown surface as its own bucket and inside the totals", () => {
    const s = summarizeThrottle(figure([{ modality: "typed", surface: "unknown", turns: 7 }]));
    expect(s.totalTurns).toBe(7);
    expect(s.turnsBySurface.unknown).toBe(7);
    expect(s.voiceShare).toBe(0);
  });
});

describe("summarizeRepos", () => {
  it("totals turns and distinct sessions, and finds the top repo's share", () => {
    const s = summarizeRepos([
      repo("devthrottle", 8, 6, 2),
      repo("mindzieWeb", 2, 0, 1),
    ]);
    expect(s.repoCount).toBe(2);
    expect(s.totalTurns).toBe(10);
    expect(s.totalSessions).toBe(3);
    expect(s.voiceTurns).toBe(6);
    expect(s.topRepoName).toBe("devthrottle");
    expect(s.topShare).toBeCloseTo(0.8);
    expect(s.hasData).toBe(true);
  });

  it("reports a null top share (not 0%) when nothing is counted yet", () => {
    const s = summarizeRepos([]);
    expect(s.repoCount).toBe(0);
    expect(s.totalTurns).toBe(0);
    expect(s.topShare).toBeNull();
    expect(s.topRepoName).toBeNull();
    expect(s.hasData).toBe(false);
  });
});

describe("summarizeAgents", () => {
  it("totals turns and distinct sessions, and finds the most-driven agent's share", () => {
    const s = summarizeAgents([
      agent("ClaudeCode", "Claude Code", 8, 6, 2),
      agent("Codex", "Codex", 2, 0, 1),
    ]);
    expect(s.agentCount).toBe(2);
    expect(s.totalTurns).toBe(10);
    expect(s.totalSessions).toBe(3);
    expect(s.voiceTurns).toBe(6);
    expect(s.topAgentName).toBe("Claude Code");
    expect(s.topShare).toBeCloseTo(0.8);
    expect(s.hasData).toBe(true);
  });

  // Issue #1636. Leverage is what the fleet did off the back of each turn the owner spent.
  it("computes leverage as agent-driven turns per turn you drove", () => {
    const s = summarizeAgents([
      agent("ClaudeCode", "Claude Code", 8, 6, 2, 24),
      agent("Codex", "Codex", 2, 0, 1, 6),
    ]);
    expect(s.agentDrivenTurns).toBe(30);
    expect(s.leverage).toBeCloseTo(3); // 30 agent turns off the back of 10 of yours
  });

  // The trap: agent-driven turns must never inflate the human's own numbers, or the voice share moves
  // because the definition moved rather than because the behaviour did.
  it("keeps agent-driven turns out of the human totals and the voice share", () => {
    const s = summarizeAgents([agent("Codex", "Codex", 4, 4, 1, 500)]);
    expect(s.totalTurns).toBe(4);
    expect(s.voiceTurns).toBe(4);
    expect(s.agentDrivenTurns).toBe(500);
    expect(s.topShare).toBeCloseTo(1); // still 100% of YOUR driving
  });

  // A ratio with nothing underneath it would be a fabricated number, not a big one. And an agent you drove
  // nothing through is not an agent you drove, even when the fleet drove into it.
  it("reports a null leverage (not Infinity) when you have driven no turns", () => {
    const s = summarizeAgents([agent("Codex", "Codex", 0, 0, 1, 40)]);
    expect(s.leverage).toBeNull();
    expect(s.agentDrivenTurns).toBe(40);
    expect(s.agentCount).toBe(0);
    expect(s.topAgentName).toBeNull();
    expect(s.hasData).toBe(true); // a fleet driving itself is a real state, not an empty one
  });

  it("reports a null top share (not 0%) when nothing is counted yet", () => {
    const s = summarizeAgents([]);
    expect(s.agentCount).toBe(0);
    expect(s.totalTurns).toBe(0);
    expect(s.topShare).toBeNull();
    expect(s.topAgentName).toBeNull();
    expect(s.hasData).toBe(false);
  });
});

describe("formatShare", () => {
  it("renders a fraction as a whole-number percent", () => {
    expect(formatShare(0.75)).toBe("75%");
    expect(formatShare(0)).toBe("0%");
    expect(formatShare(1)).toBe("100%");
  });

  it("renders no-data as an ASCII placeholder, never a fabricated 0%", () => {
    expect(formatShare(null)).toBe("n/a");
  });
});

describe("last24HourKeys", () => {
  it("returns 24 consecutive UTC hour keys ending at the current hour, oldest first", () => {
    const keys = last24HourKeys(new Date("2026-07-13T17:42:00Z"));
    expect(keys).toHaveLength(24);
    expect(keys[23]).toBe("2026-07-13T17"); // the hour containing "now"
    expect(keys[22]).toBe("2026-07-13T16");
    expect(keys[0]).toBe("2026-07-12T18"); // 23 hours earlier, across the day boundary
  });
});

describe("windowSeries", () => {
  it("aligns a sparse series onto the window and zero-fills the gaps", () => {
    const keys = last24HourKeys(new Date("2026-07-13T02:00:00Z"));
    const sparse: InputHour[] = [{ hour: "2026-07-13T01", turns: 5, voiceTurns: 4, typedTurns: 1 }];
    const windowed = windowSeries(sparse, keys, emptyInputHour);
    expect(windowed).toHaveLength(24);
    // The one populated hour lands in its slot; every other hour is a real zero entry.
    expect(windowed[23]).toEqual({ hour: "2026-07-13T02", turns: 0, voiceTurns: 0, typedTurns: 0 });
    expect(windowed[22]).toEqual(sparse[0]);
    expect(windowed[0].turns).toBe(0);
  });

  it("gives two different series the SAME aligned window so charts line up", () => {
    const keys = last24HourKeys(new Date("2026-07-13T10:00:00Z"));
    const a = windowSeries([{ hour: "2026-07-13T02", turns: 3, voiceTurns: 3, typedTurns: 0 }], keys, emptyInputHour);
    const b = windowSeries([{ hour: "2026-07-13T09", turns: 7, voiceTurns: 1, typedTurns: 6 }], keys, emptyInputHour);
    expect(a.map((h) => h.hour)).toEqual(b.map((h) => h.hour)); // identical hour axis -> aligned
  });
});

describe("localHourLabel", () => {
  it("formats a UTC hour key as the local 2-digit hour in the given zone", () => {
    // 17:00 UTC on a July date is 13:00 in New York (EDT, UTC-4) and 17:00 in UTC.
    expect(localHourLabel("2026-07-13T17", "UTC")).toBe("17");
    expect(localHourLabel("2026-07-13T17", "America/New_York")).toBe("13");
  });
});

describe("safeTimeZone", () => {
  it("passes a usable zone through", () => {
    expect(safeTimeZone("UTC")).toBe("UTC");
    expect(safeTimeZone("America/New_York")).toBe("America/New_York");
  });

  it("falls back to a usable zone for a bad or empty id (never throws)", () => {
    const fallback = safeTimeZone("Not/AZone");
    // Whatever it resolves to, it must be a zone Intl can actually format with.
    expect(() => new Intl.DateTimeFormat("en-US", { timeZone: fallback })).not.toThrow();
    expect(safeTimeZone("").length).toBeGreaterThan(0);
  });
});
