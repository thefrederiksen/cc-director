// The Settings tab set, shared by BOTH shells - the desktop Cockpit and the mobile app.
//
// It lives in client-core rather than in either app because the two surfaces must offer the SAME
// settings. They did not: the Cockpit had Notifications / AI / Car Mode with the microphone and
// transcription checks on a different page entirely, while the phone had a single untabbed "AI
// settings" scroll with no notification settings and no Car Mode end phrase at all. Two lists in two
// files drift apart by default; one list in one file cannot.
//
// Pure logic, no DOM, so the routing rules stay unit-testable (the repo's test convention).

/** Which shell is asking. Every caller states it - see visibleTabs. */
export type Surface = "cockpit" | "mobile";

export type TabId = "notifications" | "ai" | "transcription" | "carmode" | "injectedtext";

interface TabDef {
  id: TabId;
  label: string;
  /**
   * Where this tab appears. "all" is the default and the rule; "cockpit" is the documented exception.
   *
   * A tab is "cockpit" ONLY when the phone genuinely cannot do the job, not merely because the desktop
   * got there first - the standing rule is that Settings stays in sync and the desktop may go DEEPER,
   * never that it holds settings the phone silently lacks. Injected text qualifies: it is one
   * fleet-wide, Gateway-owned block of text, configured once at a desk, and editing it means working in
   * a wide monospace editor that has no honest phone form. Adding a second entry here should feel
   * uncomfortable and needs the same kind of reason.
   */
  surface: "all" | "cockpit";
}

// The full ordered set. The order is the order you meet them: how the fleet reaches you, what it thinks
// with, how it hears you, the hands-free mode built on all three, and last the text it hands your agents.
const ALL_TABS: TabDef[] = [
  { id: "notifications", label: "Notifications", surface: "all" },
  { id: "ai", label: "AI", surface: "all" },
  { id: "transcription", label: "Transcription", surface: "all" },
  { id: "carmode", label: "Car Mode", surface: "all" },
  { id: "injectedtext", label: "Injected text", surface: "cockpit" },
];

/**
 * The tabs to show on this surface.
 *
 * `surface` is REQUIRED rather than defaulting to "all": a caller that forgets it fails to compile,
 * instead of a phone quietly inheriting a tab it cannot render. That is the whole safety of this
 * mechanism - a default here would hand the mobile shell the Cockpit's list on the first careless call.
 */
export function visibleTabs(surface: Surface): { id: TabId; label: string }[] {
  return ALL_TABS.filter((t) => t.surface === "all" || t.surface === surface).map((t) => ({
    id: t.id,
    label: t.label,
  }));
}

/**
 * Resolve the ?tab= parameter to a tab THIS surface actually shows. Unknown, missing, retired, or
 * not-on-this-surface values fall to the first tab (Notifications).
 *
 * It is filtered by surface for the same reason visibleTabs is: a phone opening a link to
 * ?tab=injectedtext must land on a real tab, not select a tab that its own strip does not list and its
 * own panel cannot draw. A deep link is not permission to render something.
 *
 * "machine", "telemetry", and "privacy" are retired ids (the "This machine" tab left in issue #2022; the
 * old standalone Telemetry page redirected to /settings?tab=telemetry, issue #1405; the Privacy tab was
 * removed by issue #2017). They no longer resolve to a tab, so an old bookmark lands on the default rather
 * than on a tab that no longer exists.
 */
export function tabFromParam(raw: string | null, surface: Surface): TabId {
  const shown = visibleTabs(surface);
  const match = shown.find((t) => t.id === raw);
  return match ? match.id : shown[0].id;
}
