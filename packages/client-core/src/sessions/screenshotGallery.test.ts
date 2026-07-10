import { describe, expect, it } from "vitest";
import { NO_BROKEN_IMAGES, isImageBroken, markImageBroken } from "./screenshotGallery";

// Regression coverage for issue #1254: a screenshot file removed on disk must render the "image
// unavailable" placeholder, not the browser's broken-image glyph. That decision is driven by the
// broken-image tracking here, so it is verified directly.
describe("screenshots gallery broken-image tracking", () => {
  it("starts with nothing broken", () => {
    expect(isImageBroken(NO_BROKEN_IMAGES, "shot-1.png")).toBe(false);
  });

  it("marks a failed thumbnail so the panel can show the placeholder", () => {
    const next = markImageBroken(NO_BROKEN_IMAGES, "shot-1.png");

    expect(isImageBroken(next, "shot-1.png")).toBe(true);
    expect(isImageBroken(next, "shot-2.png")).toBe(false);
  });

  it("never mutates the input set, so the shared constant and prior state stay clean", () => {
    const first = markImageBroken(NO_BROKEN_IMAGES, "shot-1.png");
    const second = markImageBroken(first, "shot-2.png");

    // The shared starting constant is untouched by either call.
    expect(NO_BROKEN_IMAGES.size).toBe(0);
    // The earlier set is untouched by the later call.
    expect(isImageBroken(first, "shot-2.png")).toBe(false);
    expect(isImageBroken(second, "shot-1.png")).toBe(true);
    expect(isImageBroken(second, "shot-2.png")).toBe(true);
  });

  it("returns the same set instance for a repeated failure, to avoid a needless re-render", () => {
    const first = markImageBroken(NO_BROKEN_IMAGES, "shot-1.png");
    const again = markImageBroken(first, "shot-1.png");

    expect(again).toBe(first);
  });
});
