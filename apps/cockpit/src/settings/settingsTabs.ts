// The Settings page's tab set and the ?tab= deep-link resolution, split out so the routing rules are
// unit-testable without a DOM (the repo's test convention: pure logic, no component rendering).

export type TabId = "machine" | "notifications" | "ai" | "carmode" | "training";

export const TABS: { id: TabId; label: string }[] = [
  { id: "machine", label: "This machine" },
  { id: "notifications", label: "Notifications" },
  { id: "ai", label: "AI" },
  { id: "carmode", label: "Car Mode" },
  { id: "training", label: "Training data" },
];

// Resolve the ?tab= parameter to a tab. Unknown or missing values fall to "This machine".
export function tabFromParam(raw: string | null): TabId {
  return TABS.some((t) => t.id === raw) ? (raw as TabId) : "machine";
}
