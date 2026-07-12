import React from "react";
import ReactDOM from "react-dom/client";
import { createBrowserRouter, RouterProvider, Navigate, Outlet } from "react-router-dom";
import { Home } from "./pages/Home";
import { NewSession } from "./pages/NewSession";
import { Terminal } from "./pages/Terminal";
import { Chat } from "./pages/Chat";
import { FileView } from "./pages/FileView";
import { VoiceMode } from "./pages/VoiceMode";
import { CarMode } from "./pages/CarMode";
import { AiSettings } from "./pages/AiSettings";
import { About } from "./pages/About";
import { YourThrottle } from "./pages/YourThrottle";
import { SignIn } from "@devthrottle/client-core/auth/SignIn";
import { DeviceCallback } from "@devthrottle/client-core/auth/DeviceCallback";
import { hasDeviceKey } from "@devthrottle/client-core/auth/deviceKey";
import { ensureGatewayCookie, configureUnauthorizedRedirect, mobileSignInRedirect } from "@devthrottle/client-core/api/client";
import { ensurePushSubscribed } from "@devthrottle/client-core/push/register";
import { CreditsNotice } from "./components/CreditsNotice";
import { ConnectionBanner } from "./components/ConnectionBanner";
import { useScreenWakeLock } from "./hooks/useScreenWakeLock";
import { resumePendingDictations } from "@devthrottle/client-core/dictation/backgroundSend";
import { RouteRecoveryBoundary, RootLayout } from "./components/StaleShellRecovery";
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
  // Resume any recorded-but-unsent dictation once the phone is enrolled (issue #1006): a clip whose
  // upload was interrupted by a refresh / crash / dropped connection is re-driven to the session here.
  React.useEffect(() => {
    void resumePendingDictations();
  }, []);
  // The one global bad-connection banner (mobile-resilience mission): mounted once here so it pins to
  // the top of every gated screen and is the single voice for a bad connection. Hidden while the
  // connection is good; the pages keep their last-known content underneath either way.
  return (
    <>
      <ConnectionBanner />
      <Outlet />
    </>
  );
}

// Mirror the enrolled per-device key into the cc-gateway-token cookie at startup so the live
// terminal WebSocket (which cannot carry an Authorization header) authenticates same-origin to the
// Gateway. The key is already in this origin's storage, so the cookie exposes nothing new; this is a
// no-op before the phone has enrolled. (No token is injected into the page - issue #908.)
ensureGatewayCookie();

// Re-gate a mid-session 401 (a revoked device key) through THIS shell's own /m/signin enrollment
// screen. This is the mobile default in shared client-core, but each shell installs its own redirect
// so the desktop Cockpit can install its own /signin flow instead (issues #1024/#1088); installing it
// here explicitly keeps the mobile shell self-documenting about its own re-gate entry.
configureUnauthorizedRedirect(mobileSignInRedirect);

// If the user already granted notification permission on a previous visit, silently refresh the push
// subscription so the Gateway's record stays current (subscriptions can rotate). Never prompts here -
// that needs a user gesture (the "Enable notifications" button on the roster). Non-fatal on failure.
void ensurePushSubscribed();

// Force the newest bundle into a long-lived Progressive Web App. The service worker is network-first for
// the shell + bundle (vite.config.ts), so a plain reopen already fetches the latest; this handles the
// other case - the app has been open in the background while a NEW build deployed. When the new service
// worker takes control (skipWaiting + clientsClaim on activate), reload ONCE so the page drops the old
// in-memory JS and runs the new build. Armed ONLY when the page is ALREADY controlled at load (a
// returning install): a first visit that starts uncontrolled is claimed by clientsClaim, which is NOT an
// update and must not trigger a reload. The one-shot flag prevents any reload loop.
if ("serviceWorker" in navigator && navigator.serviceWorker.controller !== null) {
  let reloadingForNewWorker = false;
  navigator.serviceWorker.addEventListener("controllerchange", () => {
    if (reloadingForNewWorker) return;
    reloadingForNewWorker = true;
    console.log("[mobile] a new service worker took control - reloading to run the latest build");
    window.location.reload();
  });
}

// The app is served under /m, so the router is rooted there. A hard navigation to a deep link
// (e.g. /m/session/<id>) is served the injected index.html by the Gateway and the router then
// resolves it client-side.
// All routes hang off a root layout that carries the errorElement, so a no-route match (the symptom
// of a stale service-worker shell whose router lacks the navigated route, issue #1155) is caught by
// RouteRecoveryBoundary and self-healed instead of dead-ending on React Router's raw 404.
const router = createBrowserRouter(
  [
    {
      element: <RootLayout />,
      errorElement: <RouteRecoveryBoundary />,
      children: [
        // Ungated: reachable before the phone has enrolled.
        { path: "/signin", element: <SignIn /> },
        { path: "/device-callback", element: <DeviceCallback /> },
        // Gated: everything real requires an enrolled device key.
        {
          element: <RequireDeviceKey />,
          children: [
            { path: "/", element: <Home /> },
            // Car Mode (Car Mode mission): its own chrome-less, full-screen route, NOT nested in any
            // tabbed session view. Hands-free voice control of the whole fleet with a screen wake-lock.
            { path: "/car", element: <CarMode /> },
            { path: "/settings", element: <AiSettings /> },
            { path: "/about", element: <About /> },
            // Your Throttle (devthrottle-stats mission): the in-app port of the standalone Gateway
            // /stats page, reading the same GET /stats/data feed through client-core.
            { path: "/throttle", element: <YourThrottle /> },
            { path: "/new", element: <NewSession /> },
            { path: "/session/:sessionId", element: <Chat /> },
            { path: "/session/:sessionId/chat", element: <Chat /> },
            { path: "/session/:sessionId/terminal", element: <Terminal /> },
            { path: "/session/:sessionId/voice", element: <VoiceMode /> },
            // Local Files (Phase 3): the full-screen file viewer. Reached from a clicked file path in
            // the session's Chat or Terminal; the absolute path rides as ?path=. Not a tab in ViewTabs
            // (it is a leaf view of the session, dismissed with Back), matching the Cockpit modal.
            { path: "/session/:sessionId/file", element: <FileView /> },
          ],
        },
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
