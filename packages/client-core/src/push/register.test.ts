import { describe, expect, it } from "vitest";
import { notificationPermission, pushSupported, urlBase64ToUint8Array } from "./register";

// The push client is shared by both shells (mobile PWA + desktop Cockpit, issue #1257). These tests
// run in Node (no window / navigator / Notification), so they cover the pure VAPID-key conversion - the
// one subtle correctness bit - and confirm the feature gates degrade to "off / unsupported" instead of
// throwing when the browser APIs are absent (the same graceful path a non-supporting browser takes).

describe("urlBase64ToUint8Array", () => {
  it("decodes a base64url VAPID key to its exact raw bytes", () => {
    // "hello" in standard base64 is "aGVsbG8=" -> bytes 104,101,108,108,111.
    const out = urlBase64ToUint8Array("aGVsbG8");
    expect(Array.from(out)).toEqual([104, 101, 108, 108, 111]);
  });

  it("treats - and _ as the base64url alphabet (+ and /)", () => {
    // Byte sequence [255, 224] encodes to "/+A=" in standard base64 and "_-A" in base64url; both must
    // decode to the same bytes, proving the -/_ -> +// substitution.
    const fromUrl = urlBase64ToUint8Array("_-A");
    const fromStd = urlBase64ToUint8Array("/+A=");
    expect(Array.from(fromUrl)).toEqual([255, 224]);
    expect(Array.from(fromUrl)).toEqual(Array.from(fromStd));
  });

  it("pads a length that is not a multiple of four", () => {
    // A real 65-byte P-256 VAPID public key base64url-encodes to 87 chars (no padding); the decoder
    // must re-add the missing "=" and yield exactly 65 bytes.
    const key = "B".repeat(87);
    expect(urlBase64ToUint8Array(key)).toHaveLength(65);
  });
});

describe("feature gates without browser APIs", () => {
  it("pushSupported is false when navigator/window are absent", () => {
    expect(pushSupported()).toBe(false);
  });

  it("notificationPermission reports unsupported when Notification is absent", () => {
    expect(notificationPermission()).toBe("unsupported");
  });
});
