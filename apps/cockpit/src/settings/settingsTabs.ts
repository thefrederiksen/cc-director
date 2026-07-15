// The Settings page's tab set and the ?tab= deep-link resolution, split out so the routing rules are
// unit-testable without a DOM (the repo's test convention: pure logic, no component rendering).

export type TabId = "machine" | "notifications" | "ai" | "carmode" | "privacy";

export const TABS: { id: TabId; label: string }[] = [
  { id: "machine", label: "This machine" },
  { id: "notifications", label: "Notifications" },
  { id: "ai", label: "AI" },
  { id: "carmode", label: "Car Mode" },
  { id: "privacy", label: "Privacy" },
];

// Resolve the ?tab= parameter to a tab. Unknown or missing values fall to "This machine".
//
// "telemetry" is the retired standalone Telemetry page's id. That page redirected to
// /settings?tab=telemetry (issue #1405), and the setting now lives on the Privacy tab, so the id is kept
// as an alias: an old bookmark still lands on the telemetry setting rather than silently defaulting to
// "This machine".
export function tabFromParam(raw: string | null): TabId {
  if (raw === "telemetry") return "privacy";
  return TABS.some((t) => t.id === raw) ? (raw as TabId) : "machine";
}
