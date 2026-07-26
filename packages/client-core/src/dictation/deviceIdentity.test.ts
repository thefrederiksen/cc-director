import { describe, expect, it } from "vitest";
import { resolveMicrophoneIdentity, type AudioDeviceInfo } from "./deviceIdentity";

// The whole reason issue #2183 exists: a capture through the browser's default slot is labelled
// literally "Default", so every microphone the user owns reports under one name and the per-device
// comparison has nothing to compare. These tests pin the unwrapping of that slot - and the rule
// that the stable deviceId, not the display label, is what comes back as the grouping key.

function input(deviceId: string, label: string, groupId = ""): AudioDeviceInfo {
  return { kind: "audioinput", deviceId, label, groupId };
}

const JABRA = input("id-jabra", "Headset (Jabra Evolve2 65)", "group-jabra");
const REALTEK = input("id-realtek", "Microphone Array (Realtek)", "group-realtek");

describe("resolveMicrophoneIdentity", () => {
  it("returns the enumerated entry when the track already names a concrete device", () => {
    const identity = resolveMicrophoneIdentity("Headset (Jabra Evolve2 65)", "id-jabra", [JABRA, REALTEK]);
    expect(identity).toEqual({ label: "Headset (Jabra Evolve2 65)", deviceId: "id-jabra" });
  });

  it("unwraps the Chromium default slot to the real hardware via the shared groupId", () => {
    // Chromium: the track label and settings id are the virtual slot; the slot's enumerated entry
    // carries the REAL device's groupId. The resolved identity must be the hardware, not the slot.
    const slot = input("default", "Default - Headset (Jabra Evolve2 65)", "group-jabra");
    const identity = resolveMicrophoneIdentity("Default", "default", [slot, JABRA, REALTEK]);
    expect(identity).toEqual({ label: "Headset (Jabra Evolve2 65)", deviceId: "id-jabra" });
  });

  it("unwraps the communications slot the same way", () => {
    const slot = input("communications", "Communications - Microphone Array (Realtek)", "group-realtek");
    const identity = resolveMicrophoneIdentity("Communications", "communications", [slot, JABRA, REALTEK]);
    expect(identity).toEqual({ label: "Microphone Array (Realtek)", deviceId: "id-realtek" });
  });

  it("falls back to the slot label suffix when groupIds do not line up", () => {
    // Some builds stamp the slot with its own groupId. The slot label still ends with the real
    // device's name, and suffix matching is locale-independent (the "Default - " prefix is
    // translated by the browser; the device name is not).
    const slot = input("default", "Standardeinstellung - Headset (Jabra Evolve2 65)", "group-slot-own");
    const identity = resolveMicrophoneIdentity("Default", "default", [slot, JABRA, REALTEK]);
    expect(identity).toEqual({ label: "Headset (Jabra Evolve2 65)", deviceId: "id-jabra" });
  });

  it("resolves to the only real input when the slot cannot be matched any other way", () => {
    const slot = input("default", "Default", "group-slot-own");
    const identity = resolveMicrophoneIdentity("Default", "default", [slot, JABRA]);
    expect(identity).toEqual({ label: "Headset (Jabra Evolve2 65)", deviceId: "id-jabra" });
  });

  it("reports the honest raw values when the slot genuinely cannot be unwrapped", () => {
    // Two real inputs, no group match, no label suffix: inventing a name here would file
    // measurements under the wrong microphone, which is worse than an undiscriminating one.
    const slot = input("default", "Default", "group-slot-own");
    const identity = resolveMicrophoneIdentity("Default", "default", [slot, JABRA, REALTEK]);
    expect(identity).toEqual({ label: "Default", deviceId: "default" });
  });

  it("copes with an empty device list (labels withheld before permission)", () => {
    expect(resolveMicrophoneIdentity("", "", [])).toEqual({ label: "", deviceId: "" });
  });

  it("keeps the track label when the concrete enumerated entry has its label withheld", () => {
    const unnamed = input("id-x", "");
    const identity = resolveMicrophoneIdentity("Some Mic", "id-x", [unnamed]);
    expect(identity).toEqual({ label: "Some Mic", deviceId: "id-x" });
  });

  it("ignores outputs and cameras in the device list", () => {
    const speaker: AudioDeviceInfo = { kind: "audiooutput", deviceId: "id-jabra", label: "Jabra Speakers", groupId: "group-jabra" };
    const identity = resolveMicrophoneIdentity("Headset (Jabra Evolve2 65)", "id-jabra", [speaker, JABRA]);
    expect(identity).toEqual({ label: "Headset (Jabra Evolve2 65)", deviceId: "id-jabra" });
  });

  it("keeps the settings id when the id names no enumerated entry at all", () => {
    const identity = resolveMicrophoneIdentity("Ghost Mic", "id-gone", [JABRA]);
    expect(identity).toEqual({ label: "Ghost Mic", deviceId: "id-gone" });
  });
});
