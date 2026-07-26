// The Settings tab set, shared by BOTH shells - the desktop Cockpit and the mobile app.
//
// It lives in client-core rather than in either app because the two surfaces must offer the SAME
// settings. They did not: the Cockpit had Notifications / AI / Car Mode with the microphone and
// transcription checks on a different page entirely, while the phone had a single untabbed "AI
// settings" scroll with no notification settings and no Car Mode end phrase at all. Two lists in two
// files drift apart by default; one list in one file cannot.
//
// Only the LAYOUT differs between the surfaces now (the phone scrolls its tab strip sideways and
// stacks the fields full width) - never which settings exist or what they are called.
//
// Pure logic, no DOM, so the routing rules stay unit-testable (the repo's test convention).

export type TabId = "notifications" | "ai" | "transcription" | "carmode";

interface TabDef {
  id: TabId;
  label: string;
}

// The full ordered set. Identical on every surface: self-host IS the hosted Gateway with one tenant
// (issue #2022), and a phone is the same account as the desktop, so there is nothing surface-specific
// to filter out.
//
// The order is the order you meet them: how the fleet reaches you, what it thinks with, how it hears
// you, and the hands-free mode built on top of all three.
const ALL_TABS: TabDef[] = [
  { id: "notifications", label: "Notifications" },
  { id: "ai", label: "AI" },
  { id: "transcription", label: "Transcription" },
  { id: "carmode", label: "Car Mode" },
];

/** The tabs to show. The same everywhere. */
export function visibleTabs(): { id: TabId; label: string }[] {
  return ALL_TABS.map((t) => ({ id: t.id, label: t.label }));
}

/**
 * Resolve the ?tab= parameter to a tab. Unknown, missing, or now-removed values fall to the first tab
 * (Notifications).
 *
 * "machine", "telemetry", and "privacy" are retired ids (the "This machine" tab left in issue #2022; the
 * old standalone Telemetry page redirected to /settings?tab=telemetry, issue #1405; the Privacy tab was
 * removed by issue #2017). They no longer resolve to a tab, so an old bookmark lands on the default rather
 * than on a tab that no longer exists.
 */
export function tabFromParam(raw: string | null): TabId {
  const match = ALL_TABS.find((t) => t.id === raw);
  return match ? match.id : ALL_TABS[0].id;
}
