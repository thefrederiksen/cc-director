import { describe, expect, it } from "vitest";
import { portOf, repoBasename, shortId, uptime } from "./format";

// These lock the shared formatting helpers that issue #1261 consolidated: the Exes, Wingman, and
// Feedback pages deleted their local copies and now import exactly these, so a regression here would be
// a regression on all four pages at once. portOf is newly exported for the Exes page's port cell.

describe("portOf", () => {
  it("parses the port from an endpoint URL", () => {
    expect(portOf("http://127.0.0.1:7879")).toBe("7879");
    expect(portOf("http://buildbox.ts.net:7878/")).toBe("7878");
  });

  it("returns null when there is no port (the Exes cell renders '?' from this)", () => {
    expect(portOf("")).toBeNull();
    expect(portOf(null)).toBeNull();
    expect(portOf("not-a-url")).toBeNull();
  });
});

describe("repoBasename", () => {
  it("takes the leaf of a Windows or POSIX path", () => {
    expect(repoBasename("D:\\ReposFred\\devthrottle")).toBe("devthrottle");
    expect(repoBasename("/home/soren/cc-consult/")).toBe("cc-consult");
  });

  it("names an absent repository plainly", () => {
    expect(repoBasename("")).toBe("(no repo)");
    expect(repoBasename(null)).toBe("(no repo)");
  });
});

describe("shortId", () => {
  it("keeps the first eight characters", () => {
    expect(shortId("a1b2c3d4e5f6")).toBe("a1b2c3d4");
  });

  it("returns a short id unchanged", () => {
    expect(shortId("abc")).toBe("abc");
  });

  it("shows '?' for an empty identifier", () => {
    expect(shortId("")).toBe("?");
    expect(shortId(null)).toBe("?");
  });
});

describe("uptime", () => {
  const start = "2026-07-11T00:00:00Z";
  const base = Date.parse(start);

  it("renders minutes under an hour", () => {
    expect(uptime(start, base + 5 * 60_000)).toBe("5m");
  });

  it("renders hours and minutes (the Exes 'up 3h 20m' format)", () => {
    expect(uptime(start, base + (3 * 3600 + 20 * 60) * 1000)).toBe("3h 20m");
  });

  it("renders days and hours past a day", () => {
    expect(uptime(start, base + (2 * 24 * 3600 + 4 * 3600) * 1000)).toBe("2d 4h");
  });

  it("returns a dash for an absent start time", () => {
    expect(uptime(null, base)).toBe("-");
    expect(uptime("", base)).toBe("-");
  });
});
