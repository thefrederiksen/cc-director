import { describe, it, expect } from "vitest";
import { finalParagraph } from "./briefFallback";

// Mirrors the C# BriefBuilderTests FallbackNeedsYou cases (and the Cockpit BriefFallback it derives
// from) so the Brief degrade path produces the same result in the browser as it did server-side
// (issue #970).

describe("finalParagraph", () => {
  it("returns the final non-empty paragraph", () => {
    const reply = "I did a bunch of work.\n\nHere are details.\n\nApprove 1+2 and I'll continue?";
    expect(finalParagraph(reply)).toBe("Approve 1+2 and I'll continue?");
  });

  it("handles Windows line endings", () => {
    const reply = "Work done.\r\n\r\nShall I proceed?";
    expect(finalParagraph(reply)).toBe("Shall I proceed?");
  });

  it("returns null for null/blank input", () => {
    expect(finalParagraph(null)).toBeNull();
    expect(finalParagraph(undefined)).toBeNull();
    expect(finalParagraph("   ")).toBeNull();
  });

  it("keeps the tail of an over-long paragraph", () => {
    const reply = "intro\n\n" + "a".repeat(1000);
    const got = finalParagraph(reply, 100);
    expect(got).not.toBeNull();
    expect(got!.length).toBe(100);
  });

  it("skips a trailing whitespace-only paragraph", () => {
    const reply = "The real ask is here.\n\n   ";
    expect(finalParagraph(reply)).toBe("The real ask is here.");
  });
});
