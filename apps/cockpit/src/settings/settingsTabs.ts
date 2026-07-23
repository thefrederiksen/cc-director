// The Settings page's tab set and the ?tab= deep-link resolution, split out so the routing rules are
// unit-testable without a DOM (the repo's test convention: pure logic, no component rendering).
//
// Issue #2022 - self-host IS the hosted Gateway with one tenant, so the page is IDENTICAL on both surfaces:
// per-account, three tabs, no surface branching. The "This machine" tab was retired entirely (its settings
// left the web page - diagnostics to the About page, autostart to the installer + CLI, addressing dropped,
// brain removed), and "Privacy" is gone. So the tab set no longer depends on the hosted flag at all.

export type TabId = "notifications" | "ai" | "carmode";

interface TabDef {
  id: TabId;
  label: string;
}

// The full ordered set. Identical on self-host and hosted (issue #2022) - there is nothing surface-specific
// to filter out any more.
const ALL_TABS: TabDef[] = [
  { id: "notifications", label: "Notifications" },
  { id: "ai", label: "AI" },
  { id: "carmode", label: "Car Mode" },
];

/** The tabs to show. The same on both surfaces (issue #2022). */
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
