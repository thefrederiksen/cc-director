import { Navigate, useParams } from "react-router-dom";

// Roster/detail entry-page alignment (issue #978, epic #967). The Blazor Cockpit reached a single
// session by several paths - /cockpit/{sid} (drive it) and /sessions/{sid} (the read-mostly detail
// page). The React shell has ONE session experience: the rail-plus-terminal core at /session/{sid}
// (the roster on the left, the live terminal + brief + history + action bar on the right). To keep
// every Blazor entry path reaching a page after the cutover (#979) WITHOUT standing up a duplicate
// session view, those id-carrying paths redirect into that one core. The bare list/home path
// (/cockpit) redirects to the /sessions home (the roster is the sessions list), wired in main.tsx.
export function SessionRedirect() {
  const { sessionId } = useParams();
  if (sessionId === undefined || sessionId.length === 0) {
    return <Navigate to="/sessions" replace />;
  }
  return <Navigate to={`/session/${encodeURIComponent(sessionId)}`} replace />;
}
