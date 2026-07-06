import { Link } from "react-router-dom";

// The real 404 page for the desktop Cockpit. A hard navigation to a client-side route the router
// does not know (a stale bookmark, a mistyped path) lands here instead of a developer placeholder.
// It states plainly that the page does not exist and offers the way back to Sessions (the "/" index).
export function NotFound() {
  return (
    <section className="pane">
      <h1 className="pane-title">Page not found</h1>
      <p className="pane-note">
        The page you were looking for does not exist. It may have moved, or the link may be out of
        date.
      </p>
      <Link className="pane-link" to="/">
        Go to Sessions
      </Link>
    </section>
  );
}
