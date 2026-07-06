import { NavLink, Outlet } from "react-router-dom";

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
const NAV_ITEMS: ReadonlyArray<{ to: string; label: string }> = [
  { to: "/", label: "Sessions" },
  { to: "/fleet", label: "Fleet" },
  { to: "/directors", label: "Directors" },
  { to: "/schedule", label: "Schedule" },
  { to: "/lists", label: "Lists" },
  { to: "/dictionary", label: "Dictionary" },
  { to: "/learn", label: "Learning" },
  { to: "/account", label: "Account" },
  { to: "/telemetry", label: "Telemetry" },
  { to: "/about", label: "About" },
];

// Full-load destinations (issue #978): pages the Gateway still serves as static HTML rather than React
// routes. Settings is the plain-HTML tool page (wwwroot/pages/settings.html) - re-homed into this rail
// as a root-relative anchor that does a full document load (the same behavior the Blazor NavMenu used
// with data-enhance-nav="false"), leaving the React app to land on the Gateway-served page. It is
// root-relative to the Gateway front door, never a Director address.
const EXTERNAL_ITEMS: ReadonlyArray<{ href: string; label: string }> = [
  { href: "/settings", label: "Settings" },
];

export function AppShell() {
  return (
    <div className="shell">
      <nav className="rail rail-left" aria-label="Primary">
        <div className="brand">DevThrottle</div>
        <ul className="nav">
          {NAV_ITEMS.map((item) => (
            <li key={item.to}>
              <NavLink
                to={item.to}
                end={item.to === "/"}
                className={({ isActive }) => (isActive ? "nav-link nav-link-active" : "nav-link")}
              >
                {item.label}
              </NavLink>
            </li>
          ))}
          {EXTERNAL_ITEMS.map((item) => (
            <li key={item.href}>
              {/* Full document load to a Gateway-served static page (leaves the React app). */}
              <a className="nav-link" href={item.href}>
                {item.label}
              </a>
            </li>
          ))}
        </ul>
        <div className="rail-foot">Cockpit (React) - preview</div>
      </nav>

      <main className="main-pane" aria-label="Main">
        <Outlet />
      </main>
    </div>
  );
}
