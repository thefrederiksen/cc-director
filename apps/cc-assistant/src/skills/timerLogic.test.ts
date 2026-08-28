import { describe, expect, it } from "vitest";
import {
  isSilenceCommand,
  matchTimersByName,
  sayAmbiguous,
  sayList,
  sayNotFound,
  sayStarted,
  sayStopped,
  sayStoppedAll,
  type StoredTimer,
} from "./timerLogic";

const NOW = 1_000_000;

function timer(over: Partial<StoredTimer> = {}): StoredTimer {
  return { id: 1, name: null, totalSeconds: 600, endsAt: NOW + 600_000, ringing: false, ...over };
}

describe("silencing a ringing alarm", () => {
  it("accepts the things people actually shout", () => {
    for (const said of ["stop", "Stop!", "shut up", "quiet", "be quiet", "enough", "okay", "that's enough", "turn it off"]) {
      expect(isSilenceCommand(said), said).toBe(true);
    }
  });

  it("copes with the recogniser running words together", () => {
    expect(isSilenceCommand("shutup")).toBe(true);
    expect(isSilenceCommand("ok")).toBe(true);
  });

  it("does not treat a real question as silencing", () => {
    expect(isSilenceCommand("how many grams are in an ounce")).toBe(false);
    expect(isSilenceCommand("set a timer for ten minutes")).toBe(false);
    expect(isSilenceCommand("")).toBe(false);
  });
});

describe("matching a spoken name", () => {
  const pasta = timer({ id: 1, name: "pasta" });
  const eggs = timer({ id: 2, name: "eggs" });

  it("finds a timer by its name", () => {
    expect(matchTimersByName([pasta, eggs], "pasta").matched).toEqual([pasta]);
  });

  it("finds it inside a longer phrase", () => {
    expect(matchTimersByName([pasta, eggs], "the pasta one").matched).toEqual([pasta]);
  });

  it("forgives a near miss from the recogniser", () => {
    expect(matchTimersByName([pasta, eggs], "pastas").matched).toEqual([pasta]);
  });

  it("takes the only timer when a bare 'the timer' is said", () => {
    expect(matchTimersByName([pasta], "the timer").matched).toEqual([pasta]);
  });

  // Guessing which of three timers to cancel is worse than asking.
  it("refuses to guess when 'the timer' could mean several", () => {
    const result = matchTimersByName([pasta, eggs], "the timer");
    expect(result.matched).toEqual([]);
    expect(result.problem).toBe("ambiguous");
  });

  it("takes the only timer even when the name given is wrong", () => {
    expect(matchTimersByName([pasta], "rice").matched).toEqual([pasta]);
  });

  it("reports nothing found when a wrong name is given and several are running", () => {
    expect(matchTimersByName([pasta, eggs], "rice").problem).toBe("none");
  });

  it("reports nothing found when there are no timers", () => {
    expect(matchTimersByName([], "pasta").problem).toBe("none");
  });
});

describe("what it says afterwards", () => {
  it("confirms a plain timer", () => {
    expect(sayStarted(timer())).toBe("10 minutes, starting now.");
  });

  it("confirms a named timer using its name", () => {
    expect(sayStarted(timer({ name: "pasta" }))).toBe("10 minutes for the pasta, starting now.");
  });

  it("names what it stopped", () => {
    expect(sayStopped([timer({ name: "pasta" })])).toBe("Stopped the pasta timer.");
    expect(sayStopped([timer({ name: "pasta" }), timer({ id: 2, name: "eggs" })]))
      .toBe("Stopped the pasta timer and the eggs timer.");
  });

  it("says plainly when there was nothing to stop", () => {
    expect(sayStopped([])).toBe("There was no timer to stop.");
    expect(sayStoppedAll(0)).toBe("There are no timers running.");
    expect(sayNotFound("rice")).toBe("There is no rice timer running.");
    expect(sayNotFound(null)).toBe("There is no timer running.");
  });

  it("counts when it stops everything", () => {
    expect(sayStoppedAll(1)).toBe("Stopped it.");
    expect(sayStoppedAll(3)).toBe("Stopped all 3 timers.");
  });

  it("asks which one rather than guessing", () => {
    const said = sayAmbiguous([timer({ name: "pasta" }), timer({ id: 2, name: "eggs" })]);
    expect(said).toMatch(/pasta timer and the eggs timer/);
    expect(said).toMatch(/Which one\?/);
  });

  it("lists what is running with the time left", () => {
    const said = sayList(
      [timer({ name: "pasta", endsAt: NOW + 240_000 }), timer({ id: 2, name: "eggs", endsAt: NOW + 60_000 })],
      NOW,
    );
    expect(said).toBe("4 minutes left on the pasta and 1 minute left on the eggs.");
  });

  it("mentions one that is already going off", () => {
    const said = sayList([timer({ name: "pasta", endsAt: NOW + 60_000 }), timer({ id: 2, ringing: true })], NOW);
    expect(said).toMatch(/going off now/);
  });

  it("says so when nothing is running", () => {
    expect(sayList([], NOW)).toBe("There are no timers running.");
  });
});
