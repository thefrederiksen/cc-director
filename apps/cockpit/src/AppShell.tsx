import { NavLink, Outlet, useLocation } from "react-router-dom";

// The desktop layout frame (epic #967): a two-region shell - a left rail (navigation) and the main
// pane (the routed page). The main pane fills all remaining width. Desktop-first: the frame stays
// usable down to a small laptop, which is the seam to the mobile shell.
//
// There is intentionally NO static right rail (issue #1022): an earlier port left a hardcoded
// "Awareness" placeholder rail that shipped empty on every route, duplicated the real Awareness tab
// on /session/:id, and stole ~300px of width from every page (which also worsened the terminal
// clipping). Per-page detail regions (roster, dock, awareness) belong to the routed pages
// themselves - see SessionsView - not to this frame.

// The left-rail destinations. Each maps to a real routed page (the epic ported them in one at a time).
// `subtree` marks a destination active for a route family that does NOT share its path prefix: the
// Sessions index lives at "/" (matched with `end`, so it does not light up on every route), but the
// session detail routes into "/session/:id" - a different first segment - so it needs an explicit
// subtree so Sessions stays highlighted while a session is being driven (the Directors item does not,
// because "/directors/:id" already shares the "/directors" prefix NavLink matches by default).
const NAV_ITEMS: ReadonlyArray<{ to: string; label: string; subtree?: string }> = [
  { to: "/", label: "Sessions", subtree: "/session" },
  { to: "/fleet-map", label: "Fleet Map" },
  { to: "/directors", label: "Directors" },
  { to: "/schedule", label: "Schedule" },
  // "Lists" (work lists) is hidden from the rail for now - GitHub issues already are the queue, so
  // the named-priority-list feature is unnecessary. The /lists route is left registered (so any
  // bookmarked URL still resolves) pending a decision on removing the feature entirely.
  { to: "/dictionary", label: "Dictionary" },
  { to: "/learn", label: "Learning" },
  { to: "/account", label: "Account" },
  { to: "/telemetry", label: "Telemetry" },
  { to: "/transcription", label: "Transcription" },
  { to: "/settings", label: "Settings" },
  { to: "/about", label: "About" },
];

export function AppShell() {
  const location = useLocation();

  return (
    <div className="shell">
      <nav className="rail rail-left" aria-label="Primary">
        <div className="brand">DevThrottle</div>
        <ul className="nav">
          {NAV_ITEMS.map((item) => {
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
        <div className="rail-foot">Cockpit (React)</div>
      </nav>

      <main className="main-pane" aria-label="Main">
        <Outlet />
      </main>
    </div>
  );
}
