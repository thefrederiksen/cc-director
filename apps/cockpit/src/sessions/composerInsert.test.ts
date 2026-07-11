import { describe, it, expect } from "vitest";
import { appendToCompose } from "./composerInsert";

// Issue #1266: clicking a changed file in the Source Control tab must INSERT its repository-relative
// path into the composer text box - appended at the end, separated from any existing text by a single
// space. These tests pin that behaviour (the exact rule the Source Control row's onClick relies on).
describe("appendToCompose", () => {
  it("inserts the clicked file's path into an empty composer with no leading space", () => {
    expect(appendToCompose("", "src/App.tsx")).toBe("src/App.tsx");
  });

  it("appends the path after existing text with a single separating space", () => {
    expect(appendToCompose("roll back", "src/App.tsx")).toBe("roll back src/App.tsx");
  });

  it("does not double the space when the composer already ends with one", () => {
    expect(appendToCompose("roll back ", "src/App.tsx")).toBe("roll back src/App.tsx");
  });

  it("appends a second clicked path with exactly one space between the two paths", () => {
    const afterFirst = appendToCompose("", "src/App.tsx");
    expect(appendToCompose(afterFirst, "docs/README.md")).toBe("src/App.tsx docs/README.md");
  });

  it("leaves the composer unchanged when there is nothing to insert", () => {
    expect(appendToCompose("roll back", "")).toBe("roll back");
  });
});
