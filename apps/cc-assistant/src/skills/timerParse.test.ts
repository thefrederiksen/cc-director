import { describe, expect, it } from "vitest";
import { clockFace, parseTimer, spokenDuration } from "./timerParse";

function seconds(command: string): number | null {
  const intent = parseTimer(command);
  return intent !== null && intent.kind === "start" ? intent.seconds : null;
}

describe("parseTimer, starting one", () => {
  it("reads the ordinary phrasing", () => {
    expect(seconds("set a timer for ten minutes")).toBe(600);
    expect(seconds("set a timer for 10 minutes")).toBe(600);
    expect(seconds("timer for five minutes")).toBe(300);
    expect(seconds("set a 3 minute timer")).toBe(180);
    expect(seconds("start a timer for ninety seconds")).toBe(90);
  });

  it("reads hours and mixed durations", () => {
    expect(seconds("set a timer for one hour")).toBe(3600);
    expect(seconds("set a timer for one hour and thirty minutes")).toBe(5400);
    expect(seconds("set a timer for 2 hours 15 minutes")).toBe(8100);
  });

  it("adds numbers said as a pair", () => {
    expect(seconds("set a timer for twenty five minutes")).toBe(1500);
  });

  it("treats a bare unit as one of it", () => {
    expect(seconds("set a timer for a minute")).toBe(60);
    expect(seconds("set an hour timer")).toBe(3600);
  });

  it("copes with the short forms a recogniser produces", () => {
    expect(seconds("set a timer for 5 mins")).toBe(300);
    expect(seconds("timer 30 secs")).toBe(30);
  });
});

describe("parseTimer, leaving other things alone", () => {
  // The important half. Anything this swallows never reaches the model.
  it("ignores sentences that are not about timers", () => {
    expect(parseTimer("how many grams are in an ounce")).toBeNull();
    expect(parseTimer("what is the capital of denmark")).toBeNull();
    expect(parseTimer("tell me a joke")).toBeNull();
    expect(parseTimer("play some music for ten minutes")).toBeNull();
  });

  it("ignores a timer request with no duration in it", () => {
    expect(parseTimer("set a timer")).toBeNull();
  });

  it("ignores an absurd duration, which is a mishearing", () => {
    expect(parseTimer("set a timer for 90 hours")).toBeNull();
  });

  it("ignores nothing at all", () => {
    expect(parseTimer("")).toBeNull();
    expect(parseTimer("   ")).toBeNull();
  });
});

describe("parseTimer, cancelling and asking", () => {
  it("recognises cancelling", () => {
    expect(parseTimer("cancel the timer")?.kind).toBe("cancel");
    expect(parseTimer("stop the timer")?.kind).toBe("cancel");
    expect(parseTimer("turn the alarm off")?.kind).toBe("cancel");
  });

  it("recognises asking how long is left", () => {
    expect(parseTimer("how long is left on the timer")?.kind).toBe("query");
    expect(parseTimer("how much time is left")?.kind).toBe("query");
    expect(parseTimer("check the timer")?.kind).toBe("query");
  });

  it("prefers cancelling over starting when both could match", () => {
    expect(parseTimer("cancel the ten minute timer")?.kind).toBe("cancel");
  });
});

describe("saying durations", () => {
  it("says them the way a person would", () => {
    expect(spokenDuration(600)).toBe("10 minutes");
    expect(spokenDuration(60)).toBe("1 minute");
    expect(spokenDuration(90)).toBe("1 minute and 30 seconds");
    expect(spokenDuration(5400)).toBe("1 hour and 30 minutes");
    expect(spokenDuration(30)).toBe("30 seconds");
    expect(spokenDuration(0)).toBe("0 seconds");
  });
});

describe("the clock on screen", () => {
  it("counts in minutes and seconds", () => {
    expect(clockFace(600)).toBe("10:00");
    expect(clockFace(59)).toBe("0:59");
    expect(clockFace(5)).toBe("0:05");
  });

  it("adds hours only when there are any", () => {
    expect(clockFace(3661)).toBe("1:01:01");
  });

  it("never shows a negative clock", () => {
    expect(clockFace(-5)).toBe("0:00");
  });
});
