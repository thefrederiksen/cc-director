// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, cleanup } from "@testing-library/react";
import { useDismissOnBackdrop } from "./useDismissOnBackdrop";

// Owner report: renaming a session from the Cockpit was impossible whenever you reached for the mouse.
// The rename dialog came up, you pressed inside the text box and dragged left to highlight the start of
// the name - and the dialog vanished the moment you let go of the button.
//
// The cause is how the browser targets a click: it fires on the nearest common ancestor of the press
// and the release. Press in the box, release just past the edge of the panel, and that ancestor is the
// BACKDROP - so the old `onClick={onClose}` on the backdrop ran even though the gesture began inside
// the dialog. The panel's stopPropagation could not help; the event never went near the panel.
//
// These tests model the browser exactly: the drag is a mousedown on the input followed by a click
// dispatched AT THE BACKDROP, which is what the browser really does.
//
// Revert-proof: drop the press test from useDismissOnBackdrop (go back to closing on any backdrop
// click) and "a drag out of the dialog" fails. Drop the target test and "a click inside" fails.

function Overlay({ onClose }: { onClose?: () => void }) {
  const dismiss = useDismissOnBackdrop(onClose);
  return (
    <div data-testid="backdrop" {...dismiss}>
      <div data-testid="panel" role="dialog">
        <input data-testid="name" defaultValue="devthrottle / 2905" />
        <button type="button">Save</button>
      </div>
    </div>
  );
}

describe("useDismissOnBackdrop", () => {
  beforeEach(() => cleanup());

  it("keeps the dialog open when a drag starts inside it and ends on the backdrop", () => {
    const onClose = vi.fn();
    render(<Overlay onClose={onClose} />);

    // Press in the text box, drag out, release past the edge of the panel: the browser aims the
    // resulting click at the backdrop.
    fireEvent.mouseDown(screen.getByTestId("name"));
    fireEvent.click(screen.getByTestId("backdrop"));

    expect(onClose).not.toHaveBeenCalled();
  });

  it("dismisses on a real backdrop click", () => {
    const onClose = vi.fn();
    render(<Overlay onClose={onClose} />);

    fireEvent.mouseDown(screen.getByTestId("backdrop"));
    fireEvent.click(screen.getByTestId("backdrop"));

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("ignores a click that bubbles up from inside the dialog", () => {
    const onClose = vi.fn();
    render(<Overlay onClose={onClose} />);

    fireEvent.mouseDown(screen.getByRole("button", { name: "Save" }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(onClose).not.toHaveBeenCalled();
  });

  it("does not dismiss on a click with no press behind it (keyboard activation)", () => {
    const onClose = vi.fn();
    render(<Overlay onClose={onClose} />);

    fireEvent.click(screen.getByTestId("backdrop"));

    expect(onClose).not.toHaveBeenCalled();
  });

  it("does not carry a backdrop press over to the next click", () => {
    const onClose = vi.fn();
    render(<Overlay onClose={onClose} />);

    fireEvent.mouseDown(screen.getByTestId("backdrop"));
    fireEvent.click(screen.getByTestId("backdrop"));
    // A second click with no press of its own must not dismiss again.
    fireEvent.click(screen.getByTestId("backdrop"));

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("attaches harmlessly when dismissal is switched off (mid-save dialog)", () => {
    render(<Overlay onClose={undefined} />);

    fireEvent.mouseDown(screen.getByTestId("backdrop"));
    // Nothing to assert but "it did not throw": the backdrop is simply inert while the dialog is busy.
    expect(() => fireEvent.click(screen.getByTestId("backdrop"))).not.toThrow();
  });
});
