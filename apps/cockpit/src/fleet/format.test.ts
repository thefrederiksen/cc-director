import { describe, expect, it } from "vitest";
import { portLabel, repoBasename, repoIdentity, shortId, uptime } from "./format";

// These lock the shared formatting helpers that issue #1261 consolidated: the Wingman and Feedback pages
// deleted their local copies and now import exactly these, so a regression here would be a regression on
// every one of them at once.
//
// The port parser is covered through portLabel rather than directly: it was exported for the deleted
// Exes page's port cell, and portLabel is now its only caller, so that is the surface to pin.

describe("portLabel", () => {
  it("parses the port from the control endpoint", () => {
    expect(portLabel("http://127.0.0.1:7879", null, "a1b2c3d4e5")).toBe(":7879");
  });

  it("falls back to the tailnet endpoint, trailing slash and all", () => {
    expect(portLabel(null, "http://buildbox.ts.net:7878/", "a1b2c3d4e5")).toBe(":7878");
  });

  it("shows the short id when no port can be parsed - never blank (issue #237)", () => {
    expect(portLabel("", null, "a1b2c3d4e5")).toBe("a1b2c3d4");
    expect(portLabel(null, null, "a1b2c3d4e5")).toBe("a1b2c3d4");
    expect(portLabel("not-a-url", "also-not-a-url", "a1b2c3d4e5")).toBe("a1b2c3d4");
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

describe("repoIdentity", () => {
  it("uses the GitHub owner/repo when present, ignoring the checkout folder", () => {
    // A worktree cut at "devthrottle-enroll-fix" still belongs to the repository it was cut from, so it
    // rolls up under the repoName - this is the whole point of the "By repository" pivot.
    expect(repoIdentity("thefrederiksen/devthrottle", "D:\\ReposFred\\devthrottle-enroll-fix")).toBe(
      "thefrederiksen/devthrottle",
    );
    expect(repoIdentity("thefrederiksen/devthrottle", "D:\\ReposFred\\devthrottle")).toBe(
      "thefrederiksen/devthrottle",
    );
  });

  it("folds two worktrees of one repository to the same identity", () => {
    const a = repoIdentity("thefrederiksen/devthrottle", "D:\\ReposFred\\devthrottle");
    const b = repoIdentity("thefrederiksen/devthrottle", "D:\\ReposFred\\devthrottle-enroll-fix");
    expect(a).toBe(b);
  });

  it("falls back to the working-tree leaf when there is no recognized remote", () => {
    expect(repoIdentity("", "D:\\ReposFred\\local-only")).toBe("local-only");
    expect(repoIdentity(null, "/home/soren/scratch/")).toBe("scratch");
    expect(repoIdentity(undefined, "D:\\ReposFred\\local-only")).toBe("local-only");
  });

  it("trims whitespace-only repoName and treats it as absent", () => {
    expect(repoIdentity("   ", "D:\\ReposFred\\devthrottle")).toBe("devthrottle");
  });

  it("names a session with neither remote nor path plainly", () => {
    expect(repoIdentity("", "")).toBe("(no repo)");
    expect(repoIdentity(null, null)).toBe("(no repo)");
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

  it("renders hours and minutes (the 'up 3h 20m' format)", () => {
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
