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
  /**
   * Not listed in the strip, but still REACHABLE by ?tab= on the surfaces it belongs to, and still
   * rendered in full when reached.
   *
   * This is a narrower thing than removing a tab, and the difference is the whole point of the flag. A
   * removed tab is gone for everybody. A hidden tab is one we have decided not to put in front of
   * people, while the panel behind it keeps working for whoever needs it.
   *
   * The AI tab is the reason this exists. On the hosted Gateway it shows model identities straight to
   * customers, which contradicts a rule the hosting layer already enforces one level down - speech
   * provider names are replaced with "devthrottle" for every non-admin caller. Most of the tab is inert
   * on hosted anyway: it says so itself, in its own words, because the live model catalog is refused
   * there. On a SELF-HOSTED Gateway those same pickers do real work, so deleting the component would
   * take a working feature away from self-hosters to solve a hosted presentation problem. Hidden, not
   * deleted.
   *
   * A hidden tab is still a tab: its stored settings keep being written by its own controls when it is
   * reached, and keep being read by the product. Hiding a control must never quietly reset what it
   * holds - those settings live on the Gateway, per account, and nothing in this file touches them.
   */
  hidden?: true;
}

// The full ordered set. The order is the order you meet them: how the fleet reaches you, what it thinks
// with, how it hears you, the hands-free mode built on all three, and last the text it hands your agents.
const ALL_TABS: TabDef[] = [
  { id: "notifications", label: "Notifications", surface: "all" },
  { id: "ai", label: "AI", surface: "all", hidden: true },
  { id: "transcription", label: "Transcription", surface: "all" },
  { id: "carmode", label: "Car Mode", surface: "all" },
  { id: "injectedtext", label: "Injected text", surface: "cockpit" },
];

/**
 * Every tab this surface OWNS - listed or not. Not exported: it is the input to the two rules below,
 * which are the only two questions a caller gets to ask.
 *
 * `surface` is REQUIRED rather than defaulting to "all": a caller that forgets it fails to compile,
 * instead of a phone quietly inheriting a tab it cannot render. That is the whole safety of this
 * mechanism - a default here would hand the mobile shell the Cockpit's list on the first careless call.
 */
function tabsOwnedBy(surface: Surface): TabDef[] {
  return ALL_TABS.filter((t) => t.surface === "all" || t.surface === surface);
}

/**
 * The tabs to SHOW on this surface - what goes in the strip.
 *
 * Hidden tabs are dropped here and only here. Everything else about them is unchanged, which is what
 * makes hiding one a reversible edit to a single line of ALL_TABS.
 */
export function visibleTabs(surface: Surface): { id: TabId; label: string }[] {
  return tabsOwnedBy(surface)
    .filter((t) => t.hidden !== true)
    .map((t) => ({ id: t.id, label: t.label }));
}

/**
 * Resolve the ?tab= parameter to a tab this surface can actually render. Unknown, missing, retired, or
 * not-on-this-surface values fall to the first VISIBLE tab (Notifications).
 *
 * It resolves against the tabs this surface OWNS, not the ones it lists, so a hidden tab stays reachable
 * by its own link. That is deliberate, and it is the half of hiding that is easy to leave out: drop a tab
 * from the strip alone and every existing link to it silently lands on Notifications instead, which reads
 * as the page having lost the setting rather than as the tab having been tidied away.
 *
 * It is still filtered by surface, for the same reason visibleTabs is: a phone opening a link to
 * ?tab=injectedtext must land on a real tab, not select a tab whose panel it cannot draw. A deep link is
 * permission to reach a tab this surface owns - never permission to render one it does not.
 *
 * "machine", "telemetry", and "privacy" are retired ids (the "This machine" tab left in issue #2022; the
 * old standalone Telemetry page redirected to /settings?tab=telemetry, issue #1405; the Privacy tab was
 * removed by issue #2017). They no longer resolve to a tab, so an old bookmark lands on the default rather
 * than on a tab that no longer exists.
 */
export function tabFromParam(raw: string | null, surface: Surface): TabId {
  const match = tabsOwnedBy(surface).find((t) => t.id === raw);
  return match ? match.id : visibleTabs(surface)[0].id;
}
