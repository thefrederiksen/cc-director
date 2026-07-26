// Resolving WHICH microphone actually recorded a dictation (issue #2183).
//
// The track label alone is not enough. When the user has not chosen a specific input, the browser
// opens the *default device slot* and labels the track literally "Default" (Chromium also has a
// second virtual slot, "Communications", on Windows). Every microphone the user ever plugs in then
// reports under the same name, and the per-device quality comparison - the whole point of recording
// the name - has nothing to compare.
//
// The resolution below turns (track label, live track's deviceId, the enumerateDevices list) into
// the real hardware behind the slot. It only works while a capture is live, because a browser
// withholds device labels until microphone permission has been granted - which is exactly when the
// recorder calls it.
//
// WHY THE ID IS STORED ALONGSIDE THE LABEL. The label is display metadata: a driver update or an
// operating system language change renames it, and a grouping keyed on it silently splits one
// microphone into two histories. The deviceId is stable per device per origin, so it is the value
// measurements are GROUPED by; the label is only what a human reads.

/** What the resolver returns: the display name and the stable grouping key. Either may be empty
 *  when the browser genuinely does not say - an unnamed microphone still reports its measurements. */
export interface MicrophoneIdentity {
  label: string;
  deviceId: string;
}

/** The slice of MediaDeviceInfo the resolver reads. A plain interface so tests need no browser. */
export interface AudioDeviceInfo {
  kind: string;
  deviceId: string;
  groupId: string;
  label: string;
}

/** The virtual slot ids Chromium exposes. These are ALIASES for real hardware, never hardware
 *  themselves, so neither may become a grouping key or a display name. */
const VIRTUAL_SLOT_IDS = new Set(["default", "communications"]);

/**
 * Resolve the real microphone behind a live capture. Pure so it can be tested without a browser.
 *
 * @param trackLabel the live track's label ("Default", or a real name on browsers that give one)
 * @param settingsDeviceId the live track's getSettings().deviceId - which enumerated input is in
 *        use. Firefox and Safari report the concrete id here; Chromium reports the virtual slot id
 *        when the default slot was opened, which is why the slot then has to be unwrapped.
 * @param devices the enumerateDevices() list, taken while the capture is live
 */
export function resolveMicrophoneIdentity(
  trackLabel: string,
  settingsDeviceId: string,
  devices: readonly AudioDeviceInfo[],
): MicrophoneIdentity {
  const inputs = devices.filter((d) => d.kind === "audioinput");
  const fallback: MicrophoneIdentity = { label: trackLabel, deviceId: settingsDeviceId };

  // The concrete case: the track already names a real enumerated input. Trust the enumerated entry
  // over the track label - it is the same string on healthy browsers, and the entry is the one that
  // carries the stable id.
  if (settingsDeviceId !== "" && !VIRTUAL_SLOT_IDS.has(settingsDeviceId)) {
    const entry = inputs.find((d) => d.deviceId === settingsDeviceId);
    if (entry === undefined) return fallback;
    return { label: entry.label !== "" ? entry.label : trackLabel, deviceId: entry.deviceId };
  }

  // The virtual-slot case. Find the slot's own entry to learn which hardware it aliases.
  const slot = inputs.find((d) => VIRTUAL_SLOT_IDS.has(d.deviceId) && (settingsDeviceId === "" || d.deviceId === settingsDeviceId));
  const slotLabel = slot?.label ?? trackLabel;
  const real = inputs.filter((d) => !VIRTUAL_SLOT_IDS.has(d.deviceId));

  // Primary: Chromium stamps the slot entry with the REAL device's groupId, so the group match is
  // exact and survives every locale (the slot label's "Default - " prefix is translated; the
  // groupId is not). When several real inputs share the group, the one whose label the slot label
  // ends with is the aliased one.
  if (slot !== undefined && slot.groupId !== "") {
    const grouped = real.filter((d) => d.groupId === slot.groupId);
    const match =
      grouped.length === 1 ? grouped[0] : grouped.find((d) => d.label !== "" && slotLabel.endsWith(d.label));
    if (match !== undefined) return { label: match.label !== "" ? match.label : trackLabel, deviceId: match.deviceId };
  }

  // Secondary: the slot label carries the real name as a suffix ("Default - USB Microphone").
  // Matching by suffix rather than by stripping a prefix keeps this locale-independent too.
  const bySuffix = real.find((d) => d.label !== "" && slotLabel !== "" && slotLabel.endsWith(d.label) && slotLabel !== d.label);
  if (bySuffix !== undefined) return { label: bySuffix.label, deviceId: bySuffix.deviceId };

  // Last resort: only one real input exists, so the slot can only be aliasing it.
  if (real.length === 1 && real[0].label !== "") return { label: real[0].label, deviceId: real[0].deviceId };

  // The slot could not be unwrapped (an empty device list, or labels withheld). Report what the
  // track said - an honest "Default" beats an invented name - and keep whatever id there was so
  // measurements at least group consistently on this machine.
  return fallback;
}
