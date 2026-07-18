import { describe, expect, it } from "vitest";
import { suggestWorkflowId } from "./workflowsClient";

// The add dialog derives the workflow id from the display name; the Gateway enforces the slug shape
// server-side, so what matters here is that the suggestion always PASSES that shape (or comes out
// empty for a hopeless name, which the dialog treats as not-yet-submittable).
describe("suggestWorkflowId", () => {
  it("slugifies a display name", () => {
    expect(suggestWorkflowId("Release Train")).toBe("release-train");
    expect(suggestWorkflowId("  QA Loop!  ")).toBe("qa-loop");
    expect(suggestWorkflowId("Standalone, with review")).toBe("standalone-with-review");
  });

  it("never produces leading or trailing dashes", () => {
    expect(suggestWorkflowId("--weird--")).toBe("weird");
    expect(suggestWorkflowId("!!!")).toBe("");
  });

  it("caps at the catalog's 64-character id limit", () => {
    expect(suggestWorkflowId("x".repeat(200)).length).toBeLessThanOrEqual(64);
  });

  it("matches the Gateway's slug pattern for every non-empty suggestion", () => {
    const pattern = /^[a-z0-9][a-z0-9-]{1,63}$/;
    for (const name of ["Release Train", "a b c", "Fix #1771 spine", "My WORKFLOW 2"]) {
      const id = suggestWorkflowId(name);
      expect(id).toMatch(pattern);
    }
  });
});
