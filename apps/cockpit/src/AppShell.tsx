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

// The left-rail destinations, grouped into three labeled sections (issue #1247): Fleet (what is
// running and how it is driven), Data (the corpora and tools the fleet reads and writes), and System
// (this browser's account and the app's own settings). Every built page that is meant to be reachable
// has an entry - the pages that used to be reachable only by typing their address (Voice Recorder,
// Executables) are now in the menu.
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

const NAV_SECTIONS: ReadonlyArray<{ title: string; items: ReadonlyArray<NavItem> }> = [
  {
    title: "Fleet",
    items: [
      { to: "/fleet-map", label: "Fleet Map" },
      { to: "/sessions", label: "Sessions", subtree: "/session" },
      { to: "/directors", label: "Directors" },
      { to: "/schedule", label: "Schedule" },
    ],
  },
  {
    title: "Data",
    items: [
      { to: "/dictionary", label: "Dictionary" },
      { to: "/transcripts", label: "Voice Recorder" },
      { to: "/exes", label: "Executables" },
      { to: "/transcription", label: "Transcription" },
      { to: "/network", label: "Network" },
      { to: "/learn", label: "Learning" },
    ],
  },
  {
    title: "System",
    items: [
      { to: "/account", label: "Account" },
      { to: "/your-throttle", label: "Your Throttle" },
      { to: "/settings", label: "Settings" },
      { to: "/about", label: "About" },
    ],
  },
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
          {NAV_SECTIONS.map((section) => (
            <div className="nav-section" key={section.title}>
              <div className="nav-section-title">{section.title}</div>
              <ul className="nav-list">
                {section.items.map((item) => {
                  const inSubtree =
                    item.subtree !== undefined && location.pathname.startsWith(item.subtree);
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
            </div>
          ))}
        </div>
        <div className="rail-foot">Cockpit (React)</div>
      </nav>

      <main className="main-pane" aria-label="Main">
        <Outlet />
      </main>
    </div>
  );
}
