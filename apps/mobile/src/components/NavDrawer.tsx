import { useState } from "react";
import { Link } from "react-router-dom";

// The hamburger menu button + slide-in nav drawer for the mobile app-bar. Self-contained: it owns its
// open/closed state and renders the drawer plus a dimming overlay as a fixed layer above the page.
// Tapping the overlay or any link closes it. Kept minimal and REAL - only routes that exist on the
// mobile PWA (no dead links).
export function NavDrawer() {
  const [open, setOpen] = useState(false);
  const close = () => setOpen(false);

  return (
    <>
      <button type="button" className="hamburger" aria-label="Open menu" aria-expanded={open} onClick={() => setOpen(true)}>
        <span />
        <span />
        <span />
      </button>

      {open && (
        <div className="drawer-overlay" onClick={close} role="presentation">
          <nav className="drawer" aria-label="Navigation" onClick={(e) => e.stopPropagation()}>
            <div className="drawer-head">
              <span className="drawer-title">DevThrottle</span>
              <button type="button" className="drawer-close" aria-label="Close menu" onClick={close}>
                x
              </button>
            </div>
            <Link className="drawer-item" to="/" onClick={close}>
              Sessions
            </Link>
            <Link className="drawer-item" to="/new" onClick={close}>
              New session
            </Link>
            <Link className="drawer-item" to="/car" onClick={close}>
              Car Mode
            </Link>
            <div className="drawer-sep" />
            <Link className="drawer-item" to="/settings" onClick={close}>
              AI settings
            </Link>
            <Link className="drawer-item" to="/about" onClick={close}>
              About
            </Link>
          </nav>
        </div>
      )}
    </>
  );
}
