import React from "react";
import ReactDOM from "react-dom/client";
import { createBrowserRouter, Navigate, Outlet, RouterProvider, useLocation } from "react-router-dom";
import { ensureGatewayCookie, configureUnauthorizedRedirect, cockpitSignInRedirect } from "@devthrottle/client-core/api/client";
import { SignIn } from "@devthrottle/client-core/auth/SignIn";
import { DeviceCallback } from "@devthrottle/client-core/auth/DeviceCallback";
import { hasDeviceKey } from "@devthrottle/client-core/auth/deviceKey";
import { configureEnrollment, COCKPIT_ENROLLMENT_PROFILE } from "@devthrottle/client-core/auth/enrollRequest";
import { ensurePushSubscribed } from "@devthrottle/client-core/push/register";
import { registerCockpitServiceWorker } from "./push/registerSw";
import { AppShell } from "./AppShell";
import { NotFound } from "./panes/NotFound";
import { SessionsEmpty, SessionsView } from "./sessions/SessionsView";
import { SessionDetail } from "./sessions/SessionDetail";
import { SessionRedirect } from "./sessions/SessionRedirect";
import { FleetMapView } from "./fleet/FleetMapView";
import { DirectorsView } from "./fleet/DirectorsView";
import { DirectorDetailView } from "./fleet/DirectorDetailView";
import { ScheduleView } from "./schedule/ScheduleView";
import { WingmanQueueView } from "./wingman/WingmanQueueView";
import { DictionaryView } from "./dictionary/DictionaryView";
import { TranscriptsView } from "./transcripts/TranscriptsView";
import { ExesView } from "./exes/ExesView";
import { LearningView } from "./learning/LearningView";
import { TelemetryView } from "./telemetry/TelemetryView";
import { TranscriptionHealthView } from "./transcription/TranscriptionHealthView";
import { AccountView } from "./account/AccountView";
import { AboutView } from "./about/AboutView";
import { SettingsView } from "./settings/SettingsView";
import { FeedbackView } from "./feedback/FeedbackView";
import "./styles.css";
import "./components/components.css";
import "./fleet/fleet.css";
import "./fleet/fleetmap.css";
import "./schedule/schedule.css";
import "./wingman/wingman.css";
import "./dictionary/dictionary.css";
import "./transcripts/transcripts.css";
import "./exes/exes.css";
import "./learning/learning.css";
import "./telemetry/telemetry.css";
import "./transcription/transcriptionhealth.css";
import "./account/account.css";
import "./about/about.css";
import "./settings/settings.css";
import "./feedback/feedback.css";

// Browser device enrollment is the front door (issue #1088): this desktop shell authenticates with
// the SAME shared client-core device-auth flow the phone shipped with (#908). The Cockpit installs its
// own enrollment profile - the /device-callback return path, the "browser" platform, a
// human-recognizable "Edge on Windows"-style device name - so the shared SignIn/DeviceCallback screens
// enroll THIS browser as a device on the account. The device key is the only standing credential.
configureEnrollment(COCKPIT_ENROLLMENT_PROFILE);

// Mirror the enrolled per-device key into the cc-gateway-token cookie at startup so the live terminal
// WebSocket (which cannot carry an Authorization header) authenticates same-origin to the Gateway.
// This is the same startup call the mobile shell makes; it exposes nothing the page does not already
// hold, and it is a no-op before this browser has enrolled.
ensureGatewayCookie();

// Re-gate a mid-session 401 (a device key revoked from the website's "Your devices") through the
// DESKTOP shell's own sign-in entry - the shared /signin enrollment flow, carrying the current route
// in next= - instead of the mobile /m/signin route baked into shared client-core (issue #1024) and
// instead of the retired /login token wall (issue #1088: a revoke returns the browser to the shared
// sign-in flow, never to login.html).
configureUnauthorizedRedirect(cockpitSignInRedirect);

// Browser notifications (issue #1257): register the Cockpit's push service worker, then - only if the
// user already granted notification permission on a previous visit - silently refresh this browser's
// push subscription so the Gateway's record stays current across subscription rotations. Neither call
// prompts (that needs a user gesture: the Settings > Notifications toggle). Both are fire-and-forget and
// non-fatal - the Cockpit works fully without notifications. This reuses the exact plumbing the phone
// shipped with (#905); there is no new Gateway code.
void registerCockpitServiceWorker().then(() => ensurePushSubscribed());

// The auth gate (issue #1088, the desktop analog of the mobile gate from #908): every real screen
// requires an enrolled device key. Without one, the browser is sent to the shared Sign in screen with
// the originally-requested route carried in next=, so it lands back on that exact route after the
// enrollment round trip. /signin and /device-callback sit OUTSIDE the gate so an unenrolled browser
// can reach them. hasDeviceKey() is read at navigation time, so enrolling (or a 401-triggered clear)
// re-gates on the next route.
function RequireDeviceKey() {
  const location = useLocation();
  if (hasDeviceKey()) return <Outlet />;
  const next = encodeURIComponent(location.pathname + location.search);
  return <Navigate to={`/signin?next=${next}`} replace />;
}

// The app is the Gateway's canonical Cockpit, served at the site root "/" (issue #979 cutover). A
// hard navigation to a deep link (e.g. /fleet) is served the index.html shell by the Gateway and the
// router then resolves it client-side.
const router = createBrowserRouter(
  [
    // Ungated: reachable before this browser has enrolled. /signin is also where the Gateway's
    // signed-out browser redirect lands (AuthMiddleware sends an unauthenticated HTML navigation to
    // /signin?next=<requested route>), and /device-callback is where devthrottle.com hands the
    // cloud device key back - in the URL fragment only, never the query (issue #1082).
    { path: "/signin", element: <SignIn /> },
    { path: "/device-callback", element: <DeviceCallback /> },
    // Gated: everything real requires an enrolled device key.
    {
      element: <RequireDeviceKey />,
      children: [
        {
          element: <AppShell />,
          children: [
            // The default landing is the Fleet Map: a fresh boot at "/" redirects there so the Cockpit
            // opens on the whole-fleet picture (the first rail item), not an empty session prompt.
            { index: true, element: <Navigate to="/fleet-map" replace /> },
            // The Sessions experience (issue #972): the fleet roster stays mounted on the left while the
            // selected session's detail (the interactive terminal from #971, the action bar, the composer,
            // the queue, and the screenshots) routes into the right region. The /sessions home shows a
            // "pick a session" prompt; /session/{sid} drives that session.
            {
              element: <SessionsView />,
              children: [
                { path: "sessions", element: <SessionsEmpty /> },
                { path: "session/:sessionId", element: <SessionDetail /> },
              ],
            },
            // Roster/detail entry-page alignment (issue #978): the Blazor Cockpit reached the one session
            // experience through several paths - /cockpit (the list/home) and /cockpit/{sid} and
            // /sessions/{sid} (drive / read-mostly detail). The React shell has ONE rail-plus-terminal
            // core (the routes above), so those Blazor paths redirect into it rather than duplicating the
            // session view. This keeps every Blazor entry path reaching a page for the cutover (#979): the
            // list/home paths land on the /sessions home, the id paths on that session in the core.
            { path: "/cockpit", element: <Navigate to="/sessions" replace /> },
            { path: "/cockpit/:sessionId", element: <SessionRedirect /> },
            { path: "/sessions/:sessionId", element: <SessionRedirect /> },
            // The fleet + machine views (issue #975): the Fleet cards view, the Directors registry
            // table, and the standalone Director-detail page. Ported one-to-one from the Blazor
            // Fleet.razor / Directors.razor / DirectorDetail.razor over the same Gateway REST surface.
            // The Fleet page was removed (issue #1212); its content is the Fleet Map's "Fleet list"
            // pivot now. Keep the old route working for bookmarks by redirecting to the Fleet Map.
            { path: "/fleet", element: <Navigate to="/fleet-map" replace /> },
            // The Fleet Map (issue #1109): the spatial node-canvas view of the same roster the Fleet
            // page lists, pivotable by machine / repository / agent. Reads the same GET /sessions
            // envelope through client-core.
            { path: "/fleet-map", element: <FleetMapView /> },
            { path: "/directors", element: <DirectorsView /> },
            { path: "/directors/:directorId", element: <DirectorDetailView /> },
            // The Schedule + Wingman-pipeline pages (issue #976): one-to-one ports of the Blazor
            // Schedule.razor (cron jobs, /cron/jobs surface) and WingmanQueue.razor (read-only
            // /wingman/queue snapshot) over the same Gateway REST surface. Both now have a Fleet-section
            // nav entry (issue #1247).
            { path: "/schedule", element: <ScheduleView /> },
            { path: "/wingman", element: <WingmanQueueView /> },
            // The tools + data pages (issue #977): one-to-one ports of the Blazor Dictionary.razor,
            // Transcripts.razor, Exes.razor, and Learning.razor over the same Gateway REST surface.
            // All four have a Data-section nav entry now (issue #1247, exposing Voice Recorder and
            // Executables that were previously reachable by address only). The Lists page was deleted
            // (issue #1247): work lists are retired - GitHub issues are the queue - so the page and its
            // /lists route are gone rather than left as dead surface.
            { path: "/dictionary", element: <DictionaryView /> },
            { path: "/transcripts", element: <TranscriptsView /> },
            { path: "/exes", element: <ExesView /> },
            { path: "/learn", element: <LearningView /> },
            // The settings/misc + account pages (issue #978, the last page-port): one-to-one ports of the
            // Blazor Telemetry.razor (fleet-wide usage-telemetry consent), Account.razor (identity + Log
            // out + Your devices), About.razor (Gateway diagnostics), and Feedback.razor (the Wingman
            // feedback corpus), each over the same Gateway REST surface. Following the Blazor nav, Account,
            // Telemetry, and About get a left-rail entry; Feedback is route-only (hidden from the default
            // rail there too), reached by its direct route.
            { path: "/telemetry", element: <TelemetryView /> },
            // Transcription Health: read-only view over the local transcription telemetry the Gateway
            // records (latency, failures, most-corrected words). Same Gateway REST surface via client-core.
            { path: "/transcription", element: <TranscriptionHealthView /> },
            { path: "/account", element: <AccountView /> },
            { path: "/about", element: <AboutView /> },
            // The Settings page (issue #1025): a real React port of the retired Blazor
            // wwwroot/pages/settings.html (the "This machine" gateway-connection tab + the "AI"
            // transcription/settings tab. The rail's Settings item used to be
            // a dead full-load anchor to /settings (nothing served it, so it fell through to "Not found");
            // it is now this route, reading/writing same-origin through the Gateway settings endpoints.
            { path: "/settings", element: <SettingsView /> },
            { path: "/feedback", element: <FeedbackView /> },
            { path: "*", element: <NotFound /> },
          ],
        },
      ],
    },
  ],
  {
    // Opt in to the React Router v7 behaviours now so the transition is a no-op and the six
    // per-page future-flag console warnings (one per unset flag) are silenced. v7_startTransition
    // is a RouterProvider flag (set on <RouterProvider> below); the rest are data-router flags.
    future: {
      v7_fetcherPersist: true,
      v7_normalizeFormMethod: true,
      v7_partialHydration: true,
      v7_relativeSplatPath: true,
      v7_skipActionErrorRevalidation: true,
    },
  }
);

const rootElement = document.getElementById("root");
if (rootElement === null) {
  throw new Error("Root element #root not found in the document");
}

ReactDOM.createRoot(rootElement).render(
  <React.StrictMode>
    <RouterProvider router={router} future={{ v7_startTransition: true }} />
  </React.StrictMode>
);
