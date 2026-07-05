import { NavLink, Outlet } from "react-router-dom";

// The desktop layout frame (epic #967): a three-region shell - a left rail (navigation), the main
// pane (the routed page), and a right rail (awareness / detail region). This scaffold renders the
// empty frame and routes between placeholder panes; the real panes are ported into these regions one
// issue at a time. Desktop-first: the frame stays usable down to a small laptop, and the right rail
// collapses around the tablet breakpoint (see styles.css), which is the seam to the mobile shell.

// The left-rail destinations. Each is a placeholder route today; porting a page (its own issue)
// swaps the placeholder for the real pane without touching this frame.
const NAV_ITEMS: ReadonlyArray<{ to: string; label: string }> = [
  { to: "/", label: "Sessions" },
  { to: "/fleet", label: "Fleet" },
  { to: "/directors", label: "Directors" },
  { to: "/schedule", label: "Schedule" },
  { to: "/lists", label: "Lists" },
  { to: "/telemetry", label: "Telemetry" },
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
        </ul>
        <div className="rail-foot">Cockpit (React) - preview</div>
      </nav>

      <main className="main-pane" aria-label="Main">
        <Outlet />
      </main>

      <aside className="rail rail-right" aria-label="Awareness">
        <div className="rail-title">Awareness</div>
        <div className="rail-empty">Turn rail and awareness panes land here.</div>
      </aside>
    </div>
  );
}
