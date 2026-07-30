import { describe, expect, it } from "vitest";
import type { SessionDto } from "../api/client";
import {
  DELIVERY_BADGE_TEXT,
  hasUndeliveredPrompt,
  promptDeliveryHistory,
  promptDeliveryNotice,
  promptDeliveryTitle,
} from "./delivery";

// A row as the Gateway stamps it. Extra keys are what the augmented type covers.
function session(extra: Record<string, unknown> = {}): SessionDto {
  return { sessionId: "s1", ...extra } as unknown as SessionDto;
}

describe("promptDeliveryNotice", () => {
  it("renders the Gateway's sentence verbatim", () => {
    const notice = "Your last prompt was not delivered - the agent never received it. The composer never echoed.";
    expect(promptDeliveryNotice(session({ promptDeliveryNotice: notice }))).toBe(notice);
  });

  it("says nothing when the Gateway stamped nothing", () => {
    expect(promptDeliveryNotice(session())).toBeNull();
  });

  it("says nothing on an old Gateway that cannot stamp it, rather than composing its own words", () => {
    // The client is dumb. Given the raw facts but no verdict, the honest answer is silence - inventing a
    // sentence here is exactly the second-answer defect the Gateway fold exists to prevent.
    const s = session({ promptDeliveryUnresolved: true, failedPromptDeliveries: 3 });
    expect(promptDeliveryNotice(s)).toBeNull();
    expect(hasUndeliveredPrompt(s)).toBe(false);
  });

  it("treats a blank stamp as nothing to say", () => {
    expect(promptDeliveryNotice(session({ promptDeliveryNotice: "   " }))).toBeNull();
  });
});

describe("hasUndeliveredPrompt", () => {
  it("is true exactly when there is a notice, so the badge and the banner cannot disagree", () => {
    expect(hasUndeliveredPrompt(session({ promptDeliveryNotice: "gone" }))).toBe(true);
    expect(hasUndeliveredPrompt(session())).toBe(false);
  });

  it("stays quiet once the failure is resolved, even with counts still on the row", () => {
    // A retry got through. The counts remain (they are the history); the alarm does not.
    const s = session({ failedPromptDeliveries: 4, composerEchoMisses: 9 });
    expect(hasUndeliveredPrompt(s)).toBe(false);
  });
});

describe("promptDeliveryHistory", () => {
  it("says nothing for a single failure and no retries - the notice already covers it", () => {
    expect(promptDeliveryHistory(session({ failedPromptDeliveries: 1 }))).toBeNull();
  });

  it("names repeated failures", () => {
    expect(promptDeliveryHistory(session({ failedPromptDeliveries: 4 }))).toBe("4 failed deliveries on this session");
  });

  it("names composer retries, singular and plural", () => {
    expect(promptDeliveryHistory(session({ composerEchoMisses: 1 }))).toBe("1 composer retry");
    expect(promptDeliveryHistory(session({ composerEchoMisses: 6 }))).toBe("6 composer retries");
  });

  it("joins both when both happened", () => {
    expect(promptDeliveryHistory(session({ failedPromptDeliveries: 2, composerEchoMisses: 6 }))).toBe(
      "2 failed deliveries on this session, 6 composer retries",
    );
  });

  it("coerces the numeric-string form the serializer can emit", () => {
    expect(promptDeliveryHistory(session({ composerEchoMisses: "3" }))).toBe("3 composer retries");
  });

  it("treats an unparseable or negative count as nothing rather than rendering junk", () => {
    expect(promptDeliveryHistory(session({ composerEchoMisses: "many", failedPromptDeliveries: -2 }))).toBeNull();
  });
});

describe("promptDeliveryTitle", () => {
  it("is the notice alone when there is no history worth adding", () => {
    const s = session({ promptDeliveryNotice: "Your last prompt was not delivered.", failedPromptDeliveries: 1 });
    expect(promptDeliveryTitle(s)).toBe("Your last prompt was not delivered.");
  });

  it("appends the history when this session has form", () => {
    const s = session({
      promptDeliveryNotice: "Your last prompt was not delivered.",
      failedPromptDeliveries: 2,
      composerEchoMisses: 6,
    });
    expect(promptDeliveryTitle(s)).toBe(
      "Your last prompt was not delivered. (2 failed deliveries on this session, 6 composer retries)",
    );
  });

  it("is null when there is no notice, so no tooltip is attached to a badge that is not there", () => {
    expect(promptDeliveryTitle(session({ failedPromptDeliveries: 9 }))).toBeNull();
  });
});

describe("the badge text", () => {
  it("names the loss rather than the mechanism", () => {
    // "not delivered" is what happened to the user. "echo miss" is what happened to the terminal, and
    // nobody reading a roster at speed can act on that.
    expect(DELIVERY_BADGE_TEXT).toBe("not delivered");
  });
});
