import { describe, expect, it } from "vitest";
import { appendEntry, awaitingConfirmation, MAX_ENTRIES, type AssistantEntry } from "./transcript";

describe("assistant transcript", () => {
  it("appends without mutating the original", () => {
    const before: AssistantEntry[] = [{ role: "user", text: "hi" }];
    const after = appendEntry(before, { role: "assistant", text: "hello" });
    expect(before).toHaveLength(1);
    expect(after).toHaveLength(2);
    expect(after[1].text).toBe("hello");
  });

  it("caps the transcript at MAX_ENTRIES, dropping the oldest", () => {
    let entries: AssistantEntry[] = [];
    for (let i = 0; i < MAX_ENTRIES + 5; i++) {
      entries = appendEntry(entries, { role: "user", text: `m${i}` });
    }
    expect(entries).toHaveLength(MAX_ENTRIES);
    expect(entries[0].text).toBe("m5");
    expect(entries[entries.length - 1].text).toBe(`m${MAX_ENTRIES + 4}`);
  });

  it("offers confirmation only while the LATEST entry is an assistant hold", () => {
    let entries = appendEntry([], { role: "user", text: "close it" });
    expect(awaitingConfirmation(entries)).toBe(false);
    entries = appendEntry(entries, { role: "assistant", text: "This is permanent. Confirm?", pendingConfirmation: true });
    expect(awaitingConfirmation(entries)).toBe(true);
    entries = appendEntry(entries, { role: "user", text: "actually, list sessions" });
    expect(awaitingConfirmation(entries)).toBe(false);
  });

  it("an error entry never reads as awaiting confirmation", () => {
    const entries = appendEntry([], { role: "error", text: "The Gateway could not be reached." });
    expect(awaitingConfirmation(entries)).toBe(false);
  });
});
