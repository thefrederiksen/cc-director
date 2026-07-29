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
            <Link className="drawer-item" to="/assistant" onClick={close}>
              Assistant
            </Link>
            <Link className="drawer-item" to="/recorder" onClick={close}>
              Voice Recorder
            </Link>
            <Link className="drawer-item" to="/throttle" onClick={close}>
              Your Throttle
            </Link>
            <Link className="drawer-item" to="/repos" onClick={close}>
              Repos
            </Link>
            <div className="drawer-sep" />
            {/* One Settings item, not three. "Test microphone" and "Test transcription" were menu
                entries of their own while they were screens of their own; they are cards on the
                Transcription tab now, alongside the transcription model and the background microphone
                measurements - the same tab the Cockpit shows. */}
            <Link className="drawer-item" to="/settings" onClick={close}>
              Settings
            </Link>
            <Link className="drawer-item" to="/diagnostics" onClick={close}>
              Diagnostics
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
