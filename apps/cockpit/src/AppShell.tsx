import { NavLink, Outlet, useLocation } from "react-router-dom";
import { useKeepWarm } from "@devthrottle/client-core/net/useKeepWarm";
import { CockpitStatusPill } from "./network/CockpitStatusPill";

// The desktop layout frame (epic #967): a two-region shell - a left rail (navigation) and the main
// pane (the routed page). The main pane fills all remaining width. Desktop-first: the frame stays
// usable down to a small laptop, which is the seam to the mobile shell.
//
// There is intentionally NO static right rail (issue #1022): an earlier port left a hardcoded
// "Awareness" placeholder rail that shipped empty on every route, duplicated the real Awareness tab
// on /session/:id, and stole ~300px of width from every page (which also worsened the terminal
// clipping). Per-page detail regions (roster, dock, awareness) belong to the routed pages
// themselves - see SessionsView - not to this frame.

// The left-rail destinations. This was three LABELED sections - Fleet, Data, System (issue #1247) -
// until the labels were removed (issue #1617). They were dim uppercase headers that lost to the item
// labels beneath them, so instead of chunking the list they added three rows of noise you scanned
// past. Raising their contrast was the obvious fix and the wrong one: it makes them louder without
// making them useful, because the grouping was not earning its keep - "Dictionary, Voice Recorder,
// Executables, Transcription, Network, Learning" under "DATA" is not a category anyone feels, it is a
// category invented to justify a header. So the list is flat now, which is what comparable product
// navigation does at this size.
//
// The one surviving grouping is positional, not labeled: the destinations about the app itself
// (Account, Your Throttle, Settings, About) sit in a second list pinned to the BOTTOM of the rail,
// away from the fleet work. That is a grouping you feel without being told.
//
// The Fleet Map is first and is the default landing (issue #1303): a fresh boot at "/" redirects to
// it, so the Cockpit opens on the whole-fleet picture (main.tsx). Sessions lives at its own /sessions
// home. `subtree` marks a destination active for a route family that does NOT share its path prefix:
// the session detail routes into "/session/:id" - a different path from "/sessions" - so Sessions
// needs an explicit subtree to stay highlighted while a session is being driven (the Directors item
// does not, because "/directors/:id" already shares the "/directors" prefix NavLink matches by
// default).
interface NavItem {
  to: string;
  label: string;
  subtree?: string;
}

// The fleet work: what is running, how it is driven, and the corpora and tools it reads and writes.
// Workflows sits with Schedule on purpose - Schedule is what runs when, Workflows is how work runs,
// and it is next to the place you start work rather than filed away under settings.
const NAV_MAIN: ReadonlyArray<NavItem> = [
  { to: "/fleet-map", label: "Fleet Map" },
  { to: "/sessions", label: "Sessions", subtree: "/session" },
  { to: "/directors", label: "Directors" },
  { to: "/schedule", label: "Schedule" },
  { to: "/workflows", label: "Workflows" },
  { to: "/dictionary", label: "Dictionary" },
  { to: "/transcripts", label: "Voice Recorder" },
  { to: "/exes", label: "Executables" },
  { to: "/transcription", label: "Transcription" },
  { to: "/network", label: "Network" },
  { to: "/learn", label: "Learning" },
];

// This browser's account and the app's own settings - pinned to the bottom of the rail.
const NAV_FOOT: ReadonlyArray<NavItem> = [
  { to: "/account", label: "Account" },
  { to: "/your-throttle", label: "Your Throttle" },
  { to: "/settings", label: "Settings" },
  { to: "/about", label: "About" },
];

export function AppShell() {
  const location = useLocation();
  // Keep-warm heartbeat (P2): hold the direct LAN path open during active use.
  useKeepWarm();

  return (
    <div className="shell">
      <nav className="rail rail-left" aria-label="Primary">
        <div className="brand">DevThrottle</div>
        <CockpitStatusPill />
        <div className="nav">
          <NavList items={NAV_MAIN} pathname={location.pathname} />
          <NavList items={NAV_FOOT} pathname={location.pathname} className="nav-list-foot" />
        </div>
        <div className="rail-foot">Cockpit (React)</div>
      </nav>

      <main className="main-pane" aria-label="Main">
        <Outlet />
      </main>
    </div>
  );
}

function NavList({
  items,
  pathname,
  className,
}: {
  items: ReadonlyArray<NavItem>;
  pathname: string;
  className?: string;
}) {
  return (
    <ul className={className === undefined ? "nav-list" : `nav-list ${className}`}>
      {items.map((item) => {
        const inSubtree = item.subtree !== undefined && pathname.startsWith(item.subtree);
        return (
          <li key={item.to}>
            <NavLink
              to={item.to}
              end={item.to === "/"}
              className={({ isActive }) =>
                isActive || inSubtree ? "nav-link nav-link-active" : "nav-link"
              }
            >
              {item.label}
            </NavLink>
          </li>
        );
      })}
    </ul>
  );
}
