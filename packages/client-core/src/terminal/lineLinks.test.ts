import { describe, it, expect } from "vitest";
import { findLineLinks } from "./lineLinks";

// Local Files mission (Phase 2): findLineLinks turns one terminal line into the clickable links on it
// with their column ranges, so the xterm link provider can underline each span and route the click.
// Full coverage is formally Phase 4; this set locks the load-bearing behavior: a detected file path and
// URL get the right [start, end) columns, the URL/path flag is preserved, and a line with no links is
// empty (so the provider returns undefined and does not decorate the line).

describe("findLineLinks", () => {
  it("returns no links for a plain line", () => {
    expect(findLineLinks("just some ordinary output text")).toEqual([]);
    expect(findLineLinks("")).toEqual([]);
  });

  it("locates an absolute Windows path with its column range", () => {
    const line = "wrote C:\\reports\\out.html done";
    const links = findLineLinks(line);
    expect(links).toHaveLength(1);
    const link = links[0];
    expect(link.text).toBe("C:\\reports\\out.html");
    expect(link.isUrl).toBe(false);
    // The range must point at the exact substring on the line.
    expect(line.slice(link.start, link.end)).toBe("C:\\reports\\out.html");
  });

  it("flags an http URL as a URL", () => {
    const line = "see https://example.com/page for details";
    const links = findLineLinks(line);
    expect(links).toHaveLength(1);
    expect(links[0].isUrl).toBe(true);
    expect(line.slice(links[0].start, links[0].end)).toBe("https://example.com/page");
  });

  it("finds a path and a URL on the same line, each positioned", () => {
    const line = "C:\\a\\b.png and http://host/x";
    const links = findLineLinks(line);
    expect(links).toHaveLength(2);
    for (const link of links) {
      expect(line.slice(link.start, link.end)).toBe(link.text);
    }
    expect(links.some((l) => !l.isUrl && l.text === "C:\\a\\b.png")).toBe(true);
    expect(links.some((l) => l.isUrl && l.text === "http://host/x")).toBe(true);
  });

  it("positions each occurrence of a path repeated on one line", () => {
    const line = "C:\\a\\b.txt then again C:\\a\\b.txt";
    const links = findLineLinks(line);
    expect(links).toHaveLength(2);
    expect(links[0].start).toBeLessThan(links[1].start);
    for (const link of links) {
      expect(line.slice(link.start, link.end)).toBe("C:\\a\\b.txt");
    }
  });
});
