import { describe, it, expect } from "vitest";
import type { HistoryBubbleFilter } from "./bubbleMapper";
import type { SessionHistoryDto } from "./types";
import { buildChatSignature, chatLinkLabel, renderChatHistory } from "./chatView";

// Covers the Chat logic hoisted out of the mobile Chat page into client-core (issue #1213), so both the
// mobile page and the Cockpit tab render from one tested source.

const ALL_HIDDEN: HistoryBubbleFilter = { showToolCalls: false, showToolResults: false, showThinking: false };

function history(messages: SessionHistoryDto["messages"], over: Partial<SessionHistoryDto> = {}): SessionHistoryDto {
  return {
    sessionId: "s",
    directorId: "d",
    agent: "ClaudeCode",
    isSupported: true,
    isRawText: false,
    historyState: "idle",
    messages,
    status: "ok",
    ...over,
  };
}

describe("renderChatHistory", () => {
  it("renders the conversation (user + assistant text) as bubbles with the default all-hidden filter", () => {
    const h = history([
      { role: "User", parts: [{ kind: "Text", text: "hello there" }] },
      { role: "Assistant", parts: [{ kind: "Text", text: "**hi** back" }] },
    ]);
    const { bubbles } = renderChatHistory(h, ALL_HIDDEN);
    expect(bubbles).toHaveLength(2);
    expect(bubbles[0].bubble.body).toContain("hello there");
    // Assistant text is rendered through Markdown (HTML disabled), so bold becomes <strong>.
    expect(bubbles[1].html).toContain("<strong>hi</strong>");
  });

  it("renders raw-text (Gemini) history verbatim, not as Markdown", () => {
    const h = history([{ role: "Assistant", parts: [{ kind: "Text", text: "raw **not bold**" }] }], {
      isRawText: true,
    });
    const { bubbles } = renderChatHistory(h, ALL_HIDDEN);
    expect(bubbles).toHaveLength(1);
    expect(bubbles[0].bubble.isRawText).toBe(true);
    expect(bubbles[0].html).toBe(""); // raw bubbles carry no rendered HTML
  });

  it("reports the unsupported-agent empty text", () => {
    const h = history([], { isSupported: false });
    const { bubbles, emptyText } = renderChatHistory(h, ALL_HIDDEN);
    expect(bubbles).toHaveLength(0);
    expect(emptyText).toBe("History is not available for this agent yet.");
  });

  it("reports the no-match empty text when everything is filtered out", () => {
    const h = history([{ role: "Assistant", parts: [{ kind: "ToolUse", text: "ls", toolName: "bash" }] }]);
    const { bubbles, emptyText } = renderChatHistory(h, ALL_HIDDEN);
    expect(bubbles).toHaveLength(0);
    expect(emptyText).toBe("No messages match the current filters.");
  });
});

describe("buildChatSignature", () => {
  it("is stable for identical input and changes when the conversation grows or the state changes", () => {
    const h = history([
      { role: "User", parts: [{ kind: "Text", text: "one" }] },
      { role: "Assistant", parts: [{ kind: "Text", text: "two" }] },
    ]);
    const bubbles = renderChatHistory(h, ALL_HIDDEN).bubbles.map((r) => r.bubble);
    const a = buildChatSignature(bubbles, "idle", ALL_HIDDEN);
    const b = buildChatSignature(bubbles, "idle", ALL_HIDDEN);
    expect(a).toBe(b);
    expect(buildChatSignature(bubbles, "working", ALL_HIDDEN)).not.toBe(a);
    expect(buildChatSignature(bubbles, "idle", { ...ALL_HIDDEN, showThinking: true })).not.toBe(a);
    expect(buildChatSignature([], "idle", ALL_HIDDEN)).not.toBe(a);
  });
});

describe("chatLinkLabel", () => {
  it("leaves a short link intact and middle-truncates a long one", () => {
    expect(chatLinkLabel("https://example.com")).toBe("https://example.com");
    const long = "https://example.com/" + "a".repeat(80);
    const label = chatLinkLabel(long);
    expect(label).toContain("...");
    expect(label.length).toBeLessThan(long.length);
    expect(label.startsWith("https://example.com/")).toBe(true);
  });
});
