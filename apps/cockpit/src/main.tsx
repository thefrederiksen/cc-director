import React from "react";
import ReactDOM from "react-dom/client";
import { createBrowserRouter, Navigate, RouterProvider } from "react-router-dom";
import { ensureGatewayCookie } from "@devthrottle/client-core/api/client";
import { AppShell } from "./AppShell";
import { PlaceholderPane } from "./panes/PlaceholderPane";
import { SessionsEmpty, SessionsView } from "./sessions/SessionsView";
import { SessionDetail } from "./sessions/SessionDetail";
import { SessionRedirect } from "./sessions/SessionRedirect";
import { FleetView } from "./fleet/FleetView";
import { DirectorsView } from "./fleet/DirectorsView";
import { DirectorDetailView } from "./fleet/DirectorDetailView";
import { ScheduleView } from "./schedule/ScheduleView";
import { WingmanQueueView } from "./wingman/WingmanQueueView";
import { ListsView } from "./lists/ListsView";
import { DictionaryView } from "./dictionary/DictionaryView";
import { TranscriptsView } from "./transcripts/TranscriptsView";
import { ExesView } from "./exes/ExesView";
import { LearningView } from "./learning/LearningView";
import { TelemetryView } from "./telemetry/TelemetryView";
import { AccountView } from "./account/AccountView";
import { AboutView } from "./about/AboutView";
import { FeedbackView } from "./feedback/FeedbackView";
import "./styles.css";
import "./fleet/fleet.css";
import "./schedule/schedule.css";
import "./wingman/wingman.css";
import "./lists/lists.css";
import "./dictionary/dictionary.css";
import "./transcripts/transcripts.css";
import "./exes/exes.css";
import "./learning/learning.css";
import "./telemetry/telemetry.css";
import "./account/account.css";
import "./about/about.css";
import "./feedback/feedback.css";

// Mirror any injected per-machine token into the cc-gateway-token cookie at startup so the live
// terminal WebSocket (which cannot carry an Authorization header) authenticates same-origin to the
// Gateway. This is the same startup call the mobile shell makes; it exposes nothing the page does not
// already hold. Wiring it now keeps the desktop shell on the shared client-core contract from day one,
// so the terminal port (issue #971) inherits an already-authenticated origin.
ensureGatewayCookie();

// The app is the Gateway's canonical Cockpit, served at the site root "/" (issue #979 cutover). A
// hard navigation to a deep link (e.g. /fleet) is served the index.html shell by the Gateway and the
// router then resolves it client-side.
const router = createBrowserRouter(
  [
    {
      element: <AppShell />,
      children: [
        // The Sessions experience (issue #972): the fleet roster stays mounted on the left while the
        // selected session's detail (the interactive terminal from #971, the action bar, the composer,
        // the queue, and the screenshots) routes into the right region. The index route ("/") shows a
        // "pick a session" prompt; /session/{sid} drives that session.
        {
          element: <SessionsView />,
          children: [
            { index: true, element: <SessionsEmpty /> },
            { path: "session/:sessionId", element: <SessionDetail /> },
          ],
        },
        // Roster/detail entry-page alignment (issue #978): the Blazor Cockpit reached the one session
        // experience through several paths - /cockpit and /sessions (the list/home) and /cockpit/{sid}
        // and /sessions/{sid} (drive / read-mostly detail). The React shell has ONE rail-plus-terminal
        // core (the routes above), so those Blazor paths redirect into it rather than duplicating the
        // session view. This keeps every Blazor entry path reaching a page for the cutover (#979): the
        // list/home paths land on the roster index, the id paths on that session in the core.
        { path: "/cockpit", element: <Navigate to="/" replace /> },
        { path: "/cockpit/:sessionId", element: <SessionRedirect /> },
        { path: "/sessions", element: <Navigate to="/" replace /> },
        { path: "/sessions/:sessionId", element: <SessionRedirect /> },
        // The fleet + machine views (issue #975): the Fleet cards dashboard, the Directors registry
        // table, and the standalone Director-detail page. Ported one-to-one from the Blazor
        // Fleet.razor / Directors.razor / DirectorDetail.razor over the same Gateway REST surface.
        { path: "/fleet", element: <FleetView /> },
        { path: "/directors", element: <DirectorsView /> },
        { path: "/directors/:directorId", element: <DirectorDetailView /> },
        // The Schedule + Wingman-pipeline pages (issue #976): one-to-one ports of the Blazor
        // Schedule.razor (cron jobs, /cron/jobs surface) and WingmanQueue.razor (read-only
        // /wingman/queue snapshot) over the same Gateway REST surface. Wingman has no left-rail nav
        // entry (mirroring the Blazor nav, which hides both from the v1 default view); it is reached
        // by its direct route, as the Blazor page is.
        { path: "/schedule", element: <ScheduleView /> },
        { path: "/wingman", element: <WingmanQueueView /> },
        // The tools + data pages (issue #977): one-to-one ports of the Blazor Lists.razor,
        // Dictionary.razor, Transcripts.razor, Exes.razor, and Learning.razor over the same Gateway
        // REST surface. Lists per-item title + flow:* status come from the Gateway item-status
        // endpoint (issue #970), never a browser-held GitHub token. Following the Blazor nav (which
        // shows Dictionary + Learning in the rail but reaches Recordings/Builds by their direct
        // route), Dictionary and Learning get a left-rail entry; Transcripts and Exes are route-only.
        { path: "/lists", element: <ListsView /> },
        { path: "/dictionary", element: <DictionaryView /> },
        { path: "/transcripts", element: <TranscriptsView /> },
        { path: "/exes", element: <ExesView /> },
        { path: "/learn", element: <LearningView /> },
        // The settings/misc + account pages (issue #978, the last page-port): one-to-one ports of the
        // Blazor Telemetry.razor (fleet-wide usage-telemetry consent), Account.razor (identity + Log
        // out + Your devices), About.razor (Gateway diagnostics), and Feedback.razor (the Wingman
        // feedback corpus), each over the same Gateway REST surface. Following the Blazor nav, Account,
        // Telemetry, and About get a left-rail entry; Feedback is route-only (hidden from the default
        // rail there too), reached by its direct route. The static /settings tool page is re-homed as a
        // full-load link in the rail (AppShell), not a React route, so it stays served by the Gateway.
        { path: "/telemetry", element: <TelemetryView /> },
        { path: "/account", element: <AccountView /> },
        { path: "/about", element: <AboutView /> },
        { path: "/feedback", element: <FeedbackView /> },
        { path: "*", element: <PlaceholderPane title="Not found" /> },
      ],
    },
  ],
);

const rootElement = document.getElementById("root");
if (rootElement === null) {
  throw new Error("Root element #root not found in the document");
}

ReactDOM.createRoot(rootElement).render(
  <React.StrictMode>
    <RouterProvider router={router} />
  </React.StrictMode>
);
