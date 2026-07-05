import React from "react";
import ReactDOM from "react-dom/client";
import { createBrowserRouter, RouterProvider, Navigate, Outlet } from "react-router-dom";
import { Home } from "./pages/Home";
import { NewSession } from "./pages/NewSession";
import { Terminal } from "./pages/Terminal";
import { Chat } from "./pages/Chat";
import { VoiceMode } from "./pages/VoiceMode";
import { AiSettings } from "./pages/AiSettings";
import { SignIn } from "@devthrottle/client-core/auth/SignIn";
import { DeviceCallback } from "@devthrottle/client-core/auth/DeviceCallback";
import { hasDeviceKey } from "@devthrottle/client-core/auth/deviceKey";
import { ensureGatewayCookie } from "@devthrottle/client-core/api/client";
import { ensurePushSubscribed } from "./push/register";
import { CreditsNotice } from "./components/CreditsNotice";
import { useScreenWakeLock } from "./hooks/useScreenWakeLock";
import "./styles.css";

// The auth gate (issue #908): every real screen requires an enrolled device key. Without one, the
// phone is sent to Sign in. /signin and /device-callback sit OUTSIDE the gate so an unenrolled phone
// can reach them. hasDeviceKey() is read at navigation time, so enrolling (or a 401-triggered
// clear) re-gates on the next route.
function RequireDeviceKey() {
  return hasDeviceKey() ? <GatedLayout /> : <Navigate to="/signin" replace />;
}

// The layout wrapping every gated page. It owns the app-level screen wake lock (issue #981) so the
// phone stays awake on ANY page (roster, Chat, Voice, New session, AI settings, Terminal) while the
// app is foregrounded. Because this layout stays mounted across navigations between gated child
// routes, the lock is acquired once (a single sentinel), not once per page.
function GatedLayout() {
  useScreenWakeLock();
  return <Outlet />;
}

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
    // Ungated: reachable before the phone has enrolled.
    { path: "/signin", element: <SignIn /> },
    { path: "/device-callback", element: <DeviceCallback /> },
    // Gated: everything real requires an enrolled device key.
    {
      element: <RequireDeviceKey />,
      children: [
        { path: "/", element: <Home /> },
        { path: "/settings", element: <AiSettings /> },
        { path: "/new", element: <NewSession /> },
        { path: "/session/:sessionId", element: <Chat /> },
        { path: "/session/:sessionId/chat", element: <Chat /> },
        { path: "/session/:sessionId/terminal", element: <Terminal /> },
        { path: "/session/:sessionId/voice", element: <VoiceMode /> },
      ],
    },
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
    <CreditsNotice />
  </React.StrictMode>
);
