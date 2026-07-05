import { describe, it, expect } from "vitest";
import { parseScreenTail } from "./briefClient";

// Mirrors the Cockpit DirectorClient.GetScreenTailAsync grid-html strip (src/CcDirector.Cockpit/
// Services/DirectorClient.cs) so the Brief's live "what is the agent doing now" tail reads the same
// in the browser as it did server-side.

describe("parseScreenTail", () => {
  it("strips span markup and decodes entities per line", () => {
    const grid =
      '<div class="line"><span class="c1">hello</span></div>' +
      '<div class="line">a &amp; b &lt;tag&gt;</div>';
    expect(parseScreenTail(grid, 8)).toBe("hello\na & b <tag>");
  });

  it("drops blank rows", () => {
    const grid =
      '<div class="line">first</div>' +
      '<div class="line">   </div>' +
      '<div class="line">second</div>';
    expect(parseScreenTail(grid, 8)).toBe("first\nsecond");
  });

  it("keeps only the last N rows", () => {
    const grid = ["one", "two", "three", "four"]
      .map((t) => `<div class="line">${t}</div>`)
      .join("");
    expect(parseScreenTail(grid, 2)).toBe("three\nfour");
  });

  it("decodes numeric entities", () => {
    const grid = '<div class="line">it&#39;s &#x41;</div>';
    expect(parseScreenTail(grid, 8)).toBe("it's A");
  });

  it("returns empty string for empty/absent input", () => {
    expect(parseScreenTail(null, 8)).toBe("");
    expect(parseScreenTail(undefined, 8)).toBe("");
    expect(parseScreenTail("", 8)).toBe("");
  });
});
