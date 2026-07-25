// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";

// Issue #2167 - the Cockpit Compact button.
//
// Compaction summarizes the conversation and carries on; clearing throws it away. They sit next to each
// other, so the tests that matter here are the ones about the DIFFERENCE: compaction never clears, the
// follow-up only goes to a driver that can time it, and a compaction nobody watched is never reported as
// "Compacted".

const { sendCompactContext, sendClearContext } = vi.hoisted(() => ({
  sendCompactContext: vi.fn(async () => ({
    submitted: true,
    compactionObserved: true,
    waitedSeconds: 41,
    continued: true,
    detail: "Compacted in 41 seconds, then sent the follow-up.",
  })),
  sendClearContext: vi.fn(async () => {}),
}));

vi.mock("@devthrottle/client-core/api/client", () => ({
  sendCompactContext,
  sendClearContext,
  sendEscape: vi.fn(async () => {}),
  sendInterrupt: vi.fn(async () => {}),
  sendHistoryPicker: vi.fn(async () => {}),
  gatewayErrorMessage: (err: Error) => err.message,
}));

import { SessionActionBar } from "./SessionActionBar";

const SESSION = "11111111-2222-3333-4444-555555555555";
const CLAUDE_CAPS = ["Cancel", "Interrupt", "ClearContext", "CompactContext", "CompactCompletionReport"];

// The action bar's own button, distinguished from the dialog's confirm button of the same name by the
// class the bar puts on its buttons. Both legitimately read "Compact" - that is the point of the label.
function compactButton(): HTMLElement {
  const match = document.querySelector<HTMLElement>("button.act-btn[title^='Summarize']");
  if (match === null) throw new Error("the action bar has no Compact button");
  return match;
}

function dialogConfirmButton(): HTMLElement {
  const match = document.querySelector<HTMLElement>(".ui-confirm-actions button:last-of-type");
  if (match === null) throw new Error("no confirmation dialog is open");
  return match;
}

async function clickCompactAndConfirm() {
  fireEvent.click(compactButton());
  await waitFor(() => expect(document.querySelector(".ui-confirm")).toBeTruthy());
  fireEvent.click(dialogConfirmButton());
}

describe("Compact button", () => {
  beforeEach(() => {
    // This project runs vitest without globals, so testing-library's automatic cleanup is not
    // registered - without this, each render leaks into the next test's document.
    cleanup();
    vi.clearAllMocks();
  });

  it("is shown for a driver that declares compaction, and hidden for one that does not", () => {
    const { unmount } = render(<SessionActionBar sessionId={SESSION} capabilities={CLAUDE_CAPS} />);
    expect(compactButton()).toBeTruthy();
    unmount();

    render(<SessionActionBar sessionId={SESSION} capabilities={["Cancel", "ClearContext"]} />);
    expect(document.querySelector("button.act-btn[title^='Summarize']")).toBeNull();
  });

  it("asks before compacting, and sends nothing if the question is not answered", () => {
    render(<SessionActionBar sessionId={SESSION} capabilities={CLAUDE_CAPS} />);

    fireEvent.click(compactButton());

    expect(sendCompactContext).not.toHaveBeenCalled();
  });

  it("compacts with a follow-up when the driver can report the finish", async () => {
    render(<SessionActionBar sessionId={SESSION} capabilities={CLAUDE_CAPS} />);

    await clickCompactAndConfirm();

    await waitFor(() => expect(sendCompactContext).toHaveBeenCalledWith(SESSION, "continue"));
  });

  // The Gateway REFUSES a continuation it cannot time. The button must know that from the declared
  // capability rather than sending one and catching the refusal.
  it("sends no follow-up to a driver that cannot report the finish", async () => {
    render(
      <SessionActionBar sessionId={SESSION} capabilities={["ClearContext", "CompactContext"]} />,
    );

    await clickCompactAndConfirm();

    await waitFor(() => expect(sendCompactContext).toHaveBeenCalledWith(SESSION, undefined));
  });

  it("never clears when asked to compact", async () => {
    render(<SessionActionBar sessionId={SESSION} capabilities={CLAUDE_CAPS} />);

    await clickCompactAndConfirm();

    await waitFor(() => expect(sendCompactContext).toHaveBeenCalled());
    expect(sendClearContext).not.toHaveBeenCalled();
  });

  it("shows the Gateway's own sentence, verbatim", async () => {
    render(<SessionActionBar sessionId={SESSION} capabilities={CLAUDE_CAPS} />);

    await clickCompactAndConfirm();

    expect(
      await screen.findByText("Compacted in 41 seconds, then sent the follow-up."),
    ).toBeTruthy();
  });

  // A compaction that was submitted but never watched is NOT a compaction anyone can vouch for. The
  // Gateway says so in its own sentence; rendering that verbatim is what keeps the screen honest, and
  // composing a cheerful message here is exactly how "Compacted" ends up on screen for one nobody saw.
  it("does not claim a compaction that was never observed", async () => {
    sendCompactContext.mockResolvedValueOnce({
      submitted: true,
      compactionObserved: false,
      waitedSeconds: 0,
      continued: false,
      detail: "Compaction submitted. Codex cannot report when it finishes, so this was not watched.",
    });
    render(
      <SessionActionBar sessionId={SESSION} capabilities={["ClearContext", "CompactContext"]} />,
    );

    await clickCompactAndConfirm();

    const status = await screen.findByText(/Compaction submitted/);
    expect(status.textContent).not.toMatch(/^Compacted/);
  });
});
