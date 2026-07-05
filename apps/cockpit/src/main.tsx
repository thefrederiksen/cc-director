import React from "react";
import ReactDOM from "react-dom/client";
import { createBrowserRouter, RouterProvider } from "react-router-dom";
import { ensureGatewayCookie } from "@devthrottle/client-core/api/client";
import { AppShell } from "./AppShell";
import { PlaceholderPane } from "./panes/PlaceholderPane";
import { SessionsEmpty, SessionsView } from "./sessions/SessionsView";
import { SessionDetail } from "./sessions/SessionDetail";
import { FleetView } from "./fleet/FleetView";
import { DirectorsView } from "./fleet/DirectorsView";
import { DirectorDetailView } from "./fleet/DirectorDetailView";
import { ScheduleView } from "./schedule/ScheduleView";
import { WingmanQueueView } from "./wingman/WingmanQueueView";
import "./styles.css";
import "./fleet/fleet.css";
import "./schedule/schedule.css";
import "./wingman/wingman.css";

// Mirror any injected per-machine token into the cc-gateway-token cookie at startup so the live
// terminal WebSocket (which cannot carry an Authorization header) authenticates same-origin to the
// Gateway. This is the same startup call the mobile shell makes; it exposes nothing the page does not
// already hold. Wiring it now keeps the desktop shell on the shared client-core contract from day one,
// so the terminal port (issue #971) inherits an already-authenticated origin.
ensureGatewayCookie();

// The app is served under /c, so the router is rooted there. A hard navigation to a deep link
// (e.g. /c/fleet) is served the index.html shell by the Gateway and the router then resolves it
// client-side. These are placeholder panes only - each real page is its own porting issue under the
// epic and replaces the matching placeholder one at a time.
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
        { path: "/lists", element: <PlaceholderPane title="Lists" /> },
        { path: "/telemetry", element: <PlaceholderPane title="Telemetry" /> },
        { path: "*", element: <PlaceholderPane title="Not found" /> },
      ],
    },
  ],
  { basename: "/c" }
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
