import { describe, expect, it } from "vitest";
import type { SessionDto } from "../api/client";
import { classify, effectiveColor } from "./ordering";

// A minimal voice-mode session for the color rule. Only the fields the rule reads are set;
// the rest of SessionDto is irrelevant here so we cast a partial.
function voice(opts: {
  color: string;
  activityState: string;
  generating: boolean;
  audioReady: boolean;
  voiceMode?: boolean;
}): SessionDto {
  return {
    sessionId: "v",
    statusColor: opts.color,
    activityState: opts.activityState,
    voiceMode: opts.voiceMode ?? true,
    voiceGenerating: opts.generating,
    voiceAudioReady: opts.audioReady,
  } as unknown as SessionDto;
}

describe("effectiveColor voice-mode preparing rule (mirrors C# SessionOrdering)", () => {
  it("uses the Gateway-stamped effective color and triage bucket when present", () => {
    const s = {
      ...voice({ color: "red", activityState: "WaitingForInput", generating: false, audioReady: false }),
      effectiveColor: "orange",
      triageBucket: "active",
    } as unknown as SessionDto;

    expect(effectiveColor(s)).toBe("orange");
    expect(classify(s)).toBe("active");
  });

  it("holds yellow while the wingman is actively generating this turn's voice", () => {
    expect(effectiveColor(voice({ color: "red", activityState: "WaitingForInput", generating: true, audioReady: false })))
      .toBe("yellow");
  });

  it("stays yellow while generating even if a stale clip is cached", () => {
    expect(effectiveColor(voice({ color: "red", activityState: "WaitingForPerm", generating: true, audioReady: true })))
      .toBe("yellow");
  });

  it("is red once audio is ready and nothing is generating", () => {
    expect(effectiveColor(voice({ color: "red", activityState: "WaitingForInput", generating: false, audioReady: true })))
      .toBe("red");
  });

  it("regression 2026-07-08: text-to-speech failure (no audio, not generating) resolves to red, NOT a stuck yellow wedge", () => {
    // A DeepInfra 504/timeout produces no audio, so the turn ends with audioReady=false and
    // nothing generating. This must become red "needs you" rather than the old permanent yellow.
    const s = voice({ color: "red", activityState: "WaitingForInput", generating: false, audioReady: false });
    expect(effectiveColor(s)).toBe("red");
    expect(classify(s)).toBe("needsYou");
  });

  it("leaves a working (blue) voice session untouched", () => {
    expect(effectiveColor(voice({ color: "blue", activityState: "Working", generating: true, audioReady: false })))
      .toBe("blue");
  });

  it("does not apply the voice rule to a non-voice session", () => {
    expect(effectiveColor(voice({ color: "red", activityState: "WaitingForInput", generating: true, audioReady: false, voiceMode: false })))
      .toBe("red");
  });
});
