import { beforeEach, describe, expect, it } from "vitest";
import { admitReport, resetReportWindow } from "./reportClientError";

describe("client error report rate cap", () => {
  beforeEach(() => resetReportWindow());

  it("admits up to the cap within one minute, then drops", () => {
    const t0 = 1_000_000;
    let admitted = 0;
    for (let i = 0; i < 60; i++) {
      if (admitReport(t0 + i * 10)) admitted++;
    }
    expect(admitted).toBe(20);
  });

  it("a new minute opens a fresh window", () => {
    const t0 = 1_000_000;
    for (let i = 0; i < 30; i++) admitReport(t0);
    expect(admitReport(t0 + 60_000)).toBe(true);
  });

  it("a render-loop burst cannot exceed the cap even across repeated calls", () => {
    const t0 = 5_000_000;
    let admitted = 0;
    for (let i = 0; i < 1000; i++) {
      if (admitReport(t0 + i)) admitted++;
    }
    expect(admitted).toBe(20);
  });
});
