import React from "react";
import ReactDOM from "react-dom/client";
import { createBrowserRouter, RouterProvider } from "react-router-dom";
import { Home } from "./pages/Home";
import { NewSession } from "./pages/NewSession";
import { Terminal } from "./pages/Terminal";
import { Chat } from "./pages/Chat";
import { VoiceMode } from "./pages/VoiceMode";
import { AiSettings } from "./pages/AiSettings";
import { ensureGatewayCookie } from "./api/client";
import { ensurePushSubscribed } from "./push/register";
import "./styles.css";

// Mirror the injected per-machine token into the cc-gateway-token cookie at startup so the live
// terminal WebSocket (which cannot carry an Authorization header) authenticates same-origin to the
// Gateway. The cookie exposes nothing the page does not already hold (window.__GW_TOKEN__).
ensureGatewayCookie();

// If the user already granted notification permission on a previous visit, silently refresh the push
// subscription so the Gateway's record stays current (subscriptions can rotate). Never prompts here -
// that needs a user gesture (the "Enable notifications" button on the roster). Non-fatal on failure.
void ensurePushSubscribed();

// The app is served under /m, so the router is rooted there. A hard navigation to a deep link
// (e.g. /m/session/<id>) is served the injected index.html by the Gateway and the router then
// resolves it client-side.
const router = createBrowserRouter(
  [
    { path: "/", element: <Home /> },
    { path: "/settings", element: <AiSettings /> },
    { path: "/new", element: <NewSession /> },
    { path: "/session/:sessionId", element: <Terminal /> },
    { path: "/session/:sessionId/chat", element: <Chat /> },
    { path: "/session/:sessionId/voice", element: <VoiceMode /> },
  ],
  { basename: "/m" }
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
