// The shared period selector, rendered (mission "Clean up Your Throttle", ruling R4). It is the ONE selector
// both shells mount, and it is dumb: the lengths, their order and their labels are the Gateway's served
// choices, the one in effect is read from the served window, and a served WEEK is shown as the selected
// item under the Gateway's own label. Choosing calls back with the length and nothing else.
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { ThrottleWindowSelector } from "./ThrottleWindowSelector";
import type { ThrottleWindow } from "./statsClient";

afterEach(cleanup);

const CHOICES = [
  { days: 1, label: "Last 24 hours" },
  { days: 7, label: "Last 7 days" },
  { days: 14, label: "Last 14 days" },
  { days: 30, label: "Last 30 days" },
];

function window(over: Partial<ThrottleWindow>): ThrottleWindow {
  return {
    fromUtc: "2026-08-29T16:00:00Z",
    toUtc: "2026-09-05T16:00:00Z",
    isDefault: true,
    label: "Last 7 days",
    kind: "default",
    days: 7,
    week: null,
    choices: CHOICES,
    ...over,
  };
}

describe("ThrottleWindowSelector", () => {
  it("renders the served choices in the served order with the served labels, and marks the one in effect", () => {
    render(<ThrottleWindowSelector window={window({})} onChoose={() => {}} />);

    const buttons = screen.getAllByRole("button");
    expect(buttons.map((b) => b.textContent)).toEqual(["Last 24 hours", "Last 7 days", "Last 14 days", "Last 30 days"]);
    expect(screen.getByRole("button", { name: "Last 7 days", pressed: true })).toBeTruthy();
    expect(screen.queryAllByRole("button", { pressed: true })).toHaveLength(1);
    expect(screen.queryByTestId("thr-window-week")).toBeNull();
  });

  it("marks a chosen length from the served window, not from anything it remembers", () => {
    render(<ThrottleWindowSelector window={window({ kind: "days", days: 14, isDefault: false, label: "Last 14 days" })} onChoose={() => {}} />);

    expect(screen.getByRole("button", { name: "Last 14 days", pressed: true })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Last 7 days", pressed: false })).toBeTruthy();
  });

  it("shows a served week as the selected item under the Gateway's label, with no length marked", () => {
    const label = "Week 35 of 2026, Monday 24 August to Sunday 30 August (America/Toronto)";
    render(
      <ThrottleWindowSelector
        window={window({ kind: "week", week: "2026-W35", days: null, isDefault: false, label })}
        onChoose={() => {}}
      />,
    );

    expect(screen.getByTestId("thr-window-week").textContent).toBe(label);
    expect(screen.getByTestId("thr-window-week").className).toContain("active");
    expect(screen.queryAllByRole("button", { pressed: true })).toHaveLength(0);
    expect(screen.getAllByRole("button")).toHaveLength(4);
  });

  it("offers only what the Gateway served - a shorter list is a shorter row", () => {
    render(<ThrottleWindowSelector window={window({ choices: CHOICES.slice(0, 2) })} onChoose={() => {}} />);
    expect(screen.getAllByRole("button").map((b) => b.textContent)).toEqual(["Last 24 hours", "Last 7 days"]);
  });

  it("calls back with the chosen length in days", () => {
    const onChoose = vi.fn();
    render(<ThrottleWindowSelector window={window({})} onChoose={onChoose} />);

    fireEvent.click(screen.getByRole("button", { name: "Last 30 days" }));

    expect(onChoose).toHaveBeenCalledTimes(1);
    expect(onChoose).toHaveBeenCalledWith(30);
  });
});
