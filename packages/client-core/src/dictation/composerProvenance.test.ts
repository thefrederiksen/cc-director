import { describe, it, expect } from "vitest";
import { ComposerProvenance } from "./composerProvenance";

// The browser composer's record of WHICH characters came from a microphone (source logging, owner's ruling
// 2026-09-05). The same semantics the desktop compose box holds in Core, held to the same cases - including
// the one that makes ranges necessary at all: the same words typed and then dictated.

const WORDS = "deploy the gateway and tell me when it is up";

describe("ComposerProvenance", () => {
  it("marks an inserted transcript, and keeps it when nothing touches it", () => {
    const box = new ComposerProvenance();
    box.inserted(WORDS, WORDS, 0, "utt-1");
    expect(box.currentSpans).toEqual([{ start: 0, length: WORDS.length, transcriptId: "utt-1" }]);
    expect(box.isWhollySpoken()).toBe(true);
  });

  it("moves a span when characters are typed before it, and leaves it when they are typed after", () => {
    const box = new ComposerProvenance();
    box.inserted(WORDS, WORDS, 0, "utt-1");
    box.textChanged(WORDS + " now", WORDS.length + 4);
    expect(box.currentSpans).toEqual([{ start: 0, length: WORDS.length, transcriptId: "utt-1" }]);
    box.textChanged("please " + WORDS + " now", 7);
    expect(box.currentSpans).toEqual([{ start: 7, length: WORDS.length, transcriptId: "utt-1" }]);
    // Typed text around it means the turn is not wholly spoken - and the characters that WERE spoken are
    // still named, which is the whole point.
    expect(box.isWhollySpoken()).toBe(false);
  });

  it("forgets a span when a character inside it is edited", () => {
    const box = new ComposerProvenance();
    box.inserted(WORDS, WORDS, 0, "utt-1");
    box.textChanged(WORDS.replace("gateway", "gateways"));
    expect(box.currentSpans).toEqual([]);
    expect(box.isWhollySpoken()).toBe(false);
  });

  it("tells the same words typed and then dictated apart by which copy was deleted", () => {
    // The case that makes a RANGE necessary: a record holding the transcript's text would see the words
    // still present and call the box spoken either way.
    const both = WORDS + " " + WORDS;
    const typedFirst = () => {
      const box = new ComposerProvenance();
      box.textChanged(WORDS);
      box.inserted(both, WORDS, WORDS.length + 1, "utt-1");
      return box;
    };

    // Delete the SPOKEN copy (the second) - the caret lands where it began, at the end of the first.
    const spokenGone = typedFirst();
    spokenGone.textChanged(WORDS, WORDS.length);
    expect(spokenGone.currentSpans).toEqual([]);
    expect(spokenGone.isWhollySpoken()).toBe(false);

    // Delete the TYPED copy (the first) - the caret lands at the front. Identical surviving text, opposite
    // answer, and only the caret separates them.
    const typedGone = typedFirst();
    typedGone.textChanged(WORDS, 0);
    expect(typedGone.currentSpans).toEqual([{ start: 0, length: WORDS.length, transcriptId: "utt-1" }]);
    expect(typedGone.isWhollySpoken()).toBe(true);
  });

  it("keeps two dictations as two spans, and the turn is then not wholly spoken", () => {
    const box = new ComposerProvenance();
    box.inserted("first check the logs", "first check the logs", 0, "utt-1");
    const both = "first check the logs " + WORDS;
    box.inserted(both, WORDS, "first check the logs ".length, "utt-2");
    expect(box.currentSpans).toEqual([
      { start: 0, length: "first check the logs".length, transcriptId: "utt-1" },
      { start: "first check the logs ".length, length: WORDS.length, transcriptId: "utt-2" },
    ]);
    expect(box.isWhollySpoken()).toBe(false);
  });

  it("projects the spans onto the text as SENT, so the claim and the text come from one string", () => {
    const box = new ComposerProvenance();
    box.textChanged("  ");
    box.inserted("  " + WORDS + "   ", WORDS, 2, "utt-1");
    const sent = box.forSend();
    expect(sent.text).toBe(WORDS);
    expect(sent.spans).toEqual([{ start: 0, length: WORDS.length, transcriptId: "utt-1" }]);
    expect(sent.text.slice(sent.spans[0].start, sent.spans[0].length)).toBe(WORDS);
  });

  it("drops a span the trim removed entirely", () => {
    const box = new ComposerProvenance();
    box.restore(WORDS + "   ", [
      { start: 0, length: WORDS.length, transcriptId: "utt-1" },
      { start: WORDS.length, length: 3, transcriptId: "utt-2" },
    ]);
    const sent = box.forSend();
    expect(sent.text).toBe(WORDS);
    expect(sent.spans).toEqual([{ start: 0, length: WORDS.length, transcriptId: "utt-1" }]);
  });

  it("forgets everything on reset, and refuses an insert whose characters are not there", () => {
    const box = new ComposerProvenance();
    box.inserted(WORDS, WORDS, 0, "utt-1");
    box.reset();
    expect(box.currentSpans).toEqual([]);
    expect(() => box.inserted(WORDS, "not in there", 0, "utt-2")).toThrow();
    expect(() => box.restore("short", [{ start: 0, length: 40 }])).toThrow();
  });

  it("survives a whole-text replacement by forgetting - a replaced box holds nothing dictated", () => {
    const box = new ComposerProvenance();
    box.inserted(WORDS, WORDS, 0, "utt-1");
    box.textChanged("/help");
    expect(box.currentSpans).toEqual([]);
  });
});
