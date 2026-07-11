import { useCallback, useEffect, useRef, useState } from "react";
import { Outlet, useMatch, useNavigate } from "react-router-dom";
import { gatewayErrorMessage, type SessionDto } from "@devthrottle/client-core/api/client";
import { getSessionsEnvelope, type DirectorReachability } from "@devthrottle/client-core/fleet/fleetClient";
import { inBucket } from "@devthrottle/client-core/sessions/ordering";
import { reconcileBadge } from "@devthrottle/client-core/push/register";
import { SessionRoster, type RosterView } from "./SessionRoster";
import { NewSessionDialog } from "./NewSessionDialog";

// The Sessions experience (issue #972): the core driving loop - see every session, select one,
// answer it. This layout route owns the fleet roster poll and the ordering-view state, renders the
// roster on the left, and routes the selected session's detail (terminal + reply/action bar) into
// the right region via <Outlet>. The roster stays mounted while you switch sessions, so the poll and
// the selection persist across navigations.
const POLL_INTERVAL_MS = 3000;
const VIEW_STORAGE_KEY = "cockpit.rosterView";

// The default ordering is "My order" (the session-rail ordering decision): attention-first is opt-in,
// never automatic. Read the last choice back so the toggle is sticky per browser, defaulting to
// My order when nothing is stored or storage is unavailable.
function initialView(): RosterView {
  try {
    return window.localStorage.getItem(VIEW_STORAGE_KEY) === "attention" ? "attention" : "my-order";
  } catch {
    return "my-order";
  }
}

/** The selected session, for the detail region to read (driver capabilities, name, hold state). */
export interface SessionsOutletContext {
  sessions: SessionDto[] | null;
  /** Per-Director reachability for the Online / Wobbly / Offline rendering (issue #1215). */
  directors: DirectorReachability[];
}

export function SessionsView() {
  const [sessions, setSessions] = useState<SessionDto[] | null>(null);
  const [directors, setDirectors] = useState<DirectorReachability[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [view, setView] = useState<RosterView>(initialView);
  const [showNew, setShowNew] = useState(false);
  const loadedOnce = useRef(false);
  const navigate = useNavigate();

  // The selected session id comes from the child route (/session/:sessionId). This layout route is an
  // ancestor of that route, so it reads the id with useMatch rather than useParams (which only exposes
  // params matched up to this route). Empty on the index route (nothing selected yet).
  const match = useMatch("/session/:sessionId");
  const selectedId = match?.params.sessionId;

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const env = await getSessionsEnvelope(signal);
      setSessions(env.sessions);
      setDirectors(env.directors);
      setError(null);
      loadedOnce.current = true;
      // Keep the browser-notification state in sync while the Cockpit is open (issue #1257): when the
      // roster shows nothing waiting, close any standing "needs you" desktop notification and clear the
      // app badge. The Gateway pushes a single zero on the falling edge too, but this clears it the
      // instant the user resolves the last session in the foreground, without waiting for that push.
      void reconcileBadge(inBucket(env.sessions, "needsYou").length);
    } catch (err) {
      if (signal?.aborted) return;
      // Keep the last-known roster on screen; only raise the stale banner (the roster component
      // decides how to show it based on whether it already has data). Map to the friendly transport
      // message so a cold start with the backend down shows "Can't reach the Gateway - retrying."
      // instead of the browser's raw "Failed to fetch" (issue #1028).
      setError(gatewayErrorMessage(err));
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    const timer = window.setInterval(() => void load(controller.signal), POLL_INTERVAL_MS);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [load]);

  const onView = useCallback((next: RosterView) => {
    setView(next);
    try {
      window.localStorage.setItem(VIEW_STORAGE_KEY, next);
    } catch {
      /* storage unavailable (private mode) - the toggle still works for this session */
    }
  }, []);

  // A new session was created (issue #1023): refresh the roster immediately so the new row shows
  // without waiting for the next poll tick, then open it in the detail region.
  const onCreated = useCallback(
    (sessionId: string) => {
      setShowNew(false);
      void load();
      navigate(`/session/${encodeURIComponent(sessionId)}`);
    },
    [load, navigate],
  );

  const context: SessionsOutletContext = { sessions, directors };

  return (
    <div className="sessions-screen">
      <SessionRoster
        sessions={sessions}
        directors={directors}
        selectedId={selectedId}
        view={view}
        onView={onView}
        error={error}
        onNewSession={() => setShowNew(true)}
      />
      <div className="sessions-detail">
        <Outlet context={context} />
      </div>
      {showNew && <NewSessionDialog onClose={() => setShowNew(false)} onCreated={onCreated} />}
    </div>
  );
}

// Shown in the detail region on the index route, before a session is selected.
export function SessionsEmpty() {
  return (
    <div className="detail-empty">
      <h1 className="detail-empty-title">Pick a session</h1>
      <p className="detail-empty-note">
        Choose a session on the left to drive it: its live terminal, the action bar, the composer, the
        prompt queue, and its screenshots.
      </p>
    </div>
  );
}
