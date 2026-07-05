import React from "react";
import ReactDOM from "react-dom/client";
import { createBrowserRouter, RouterProvider } from "react-router-dom";
import { ensureGatewayCookie } from "@devthrottle/client-core/api/client";
import { AppShell } from "./AppShell";
import { PlaceholderPane } from "./panes/PlaceholderPane";
import { TerminalPane } from "./panes/TerminalPane";
import "./styles.css";

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
        { path: "/", element: <PlaceholderPane title="Sessions" /> },
        // The live, interactive terminal for one session (issue #971). The session roster / rail
        // (issue #972) links here with a real session id; until then the pane is reachable directly
        // at /c/session/<sid>. It serves exactly one session and remounts when the id changes.
        { path: "/session/:sessionId", element: <TerminalPane /> },
        { path: "/fleet", element: <PlaceholderPane title="Fleet" /> },
        { path: "/schedule", element: <PlaceholderPane title="Schedule" /> },
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
