import React from "react";
import ReactDOM from "react-dom/client";
import { createBrowserRouter, RouterProvider, Navigate, Outlet, useLocation } from "react-router-dom";
import { Home } from "./pages/Home";
import { NewSession } from "./pages/NewSession";
import { Terminal } from "./pages/Terminal";
import { Chat } from "./pages/Chat";
import { FileView } from "./pages/FileView";
import { VoiceMode } from "./pages/VoiceMode";
import { CarMode } from "./pages/CarMode";
import { Assistant } from "./pages/Assistant";
import { EndWordTest } from "./pages/EndWordTest";
import { AiSettings } from "./pages/AiSettings";
import { About } from "./pages/About";
import { Diagnostics } from "./pages/Diagnostics";
import { YourThrottle } from "./pages/YourThrottle";
import { Repos } from "./pages/Repos";
import { SignIn } from "@devthrottle/client-core/auth/SignIn";
import { DeviceCallback } from "@devthrottle/client-core/auth/DeviceCallback";
import { hasDeviceKey } from "@devthrottle/client-core/auth/deviceKey";
import { ensureGatewayCookie, configureUnauthorizedRedirect, mobileSignInRedirect } from "@devthrottle/client-core/api/client";
import { ensurePushSubscribed } from "@devthrottle/client-core/push/register";
import { CreditsNotice } from "./components/CreditsNotice";
import { ConnectionBanner } from "./components/ConnectionBanner";
import { useVisibleViewportHeight } from "./hooks/useVisibleViewportHeight";
import { useScreenWakeLock } from "./hooks/useScreenWakeLock";
import { useKeepWarm } from "@devthrottle/client-core/net/useKeepWarm";
import { resumePendingDictations } from "@devthrottle/client-core/dictation/backgroundSend";
import { RouteRecoveryBoundary, RootLayout } from "./components/StaleShellRecovery";
import { StatusPill } from "./components/StatusPill";
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
  // THE viewport fit for the whole app: publishes the true visible height as --app-vh, which
  // .terminal-screen and .car-screen size themselves from. Mounted ONCE here so no screen has to
  // solve "does it fit?" privately again - that is why this bug kept coming back. See the hook.
  useVisibleViewportHeight();
  // Keep-warm heartbeat (P2): hold the direct LAN path open during active use so it never idles back to the relay.
  useKeepWarm();
  // Resume any recorded-but-unsent dictation once the phone is enrolled (issue #1006): a clip whose
  // upload was interrupted by a refresh / crash / dropped connection is re-driven to the session here.
  React.useEffect(() => {
    void resumePendingDictations();
  }, []);
  // The one global bad-connection banner (mobile-resilience mission): mounted once here so it pins to
  // the top of every gated screen and is the single voice for a bad connection. Hidden while the
  // connection is good; the pages keep their last-known content underneath either way.
  // The network pill is fixed top-right on every screen that has no header of its own to give it. The
  // /session/ screens DO have one and render their own inline pill there, so the fixed one stands down
  // for that whole subtree - otherwise both would show. They need that corner back for the overflow
  // menu button: while the pill held it, the button lived on the LEFT of the bar and its right-anchored
  // menu opened off the left edge of the screen.
  //
  // THIS SUPPRESSES THE PILL FOR THE ENTIRE /session/ SUBTREE, so every screen routed under /session/
  // MUST render its own <StatusPill inline />, or it will show no network status at all. Today:
  //
  //   /session/:id, /chat, /terminal, /voice  -> SessionAppBar renders it
  //   /session/:id/file                       -> FileView renders it in its own header
  //
  // The file viewer was missed when the pill was first moved inline (caught in review of #1631) - it is
  // under /session/ but does not use SessionAppBar, so it silently lost its pill. Add a new /session/
  // route and you own its pill too.
  // The roster (Home, "/") also gives the pill a real home on its header row now - an inline item in the
  // middle of the bar, the same as the session screens - so the fixed overlay stands down there too. It
  // used to sit fixed in the top-right corner and land on top of the roster's filter button.
  const pathname = useLocation().pathname;
  const onSessionScreen = pathname.startsWith("/session/");
  const onHome = pathname === "/";
  return (
    <>
      <ConnectionBanner />
      {!onSessionScreen && !onHome && <StatusPill />}
      <Outlet />
    </>
  );
}

// Mirror the enrolled per-device key into the cc-gateway-token cookie at startup so the live
// terminal WebSocket (which cannot carry an Authorization header) authenticates same-origin to the
// Gateway. The key is already in this origin's storage, so the cookie exposes nothing new; this is a
// no-op before the phone has enrolled. (No token is injected into the page - issue #908.)
ensureGatewayCookie();

// Re-gate a mid-session 401 (a revoked device key) through THIS shell's own /mobile/signin enrollment
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

// The app is served under /mobile, so the router is rooted there. A hard navigation to a deep link
// (e.g. /mobile/session/<id>) is served the injected index.html by the Gateway and the router then
// resolves it client-side. The old /m mount is 301-redirected to /mobile by the Gateway, so an
// installed PWA or a bookmark on /m/... still lands here.
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
            // The Assistant (fleet assistant build): fleet-level chat + voice, not tied to any
            // session - the phone view of the same client-core turn machine the cockpit mounts.
            // Distinct from Car Mode: button turns (tap to talk), no auto turn taking, hands-on.
            { path: "/assistant", element: <Assistant /> },
            // Car Mode End Word Test (harness for the spoken end-of-turn phrase): set a configurable
            // phrase and test detecting it live. Standalone for now; folds into the Cockpit Car Mode
            // settings tab once the approach is proven.
            { path: "/endword", element: <EndWordTest /> },
            { path: "/settings", element: <AiSettings /> },
            { path: "/about", element: <About /> },
            // Diagnostics (auto-network-switching mission): a phone-side connection tester - route
            // (direct LAN vs Tailscale relay), latency, and download/upload throughput, with a verdict.
            { path: "/diagnostics", element: <Diagnostics /> },
            // Your Throttle (devthrottle-stats mission): the in-app port of the standalone Gateway
            // /stats page, reading the same GET /stats/data feed through client-core.
            { path: "/throttle", element: <YourThrottle /> },
            // Repos (devthrottle-stats mission): the PRIVATE per-repo split, its own page separate from
            // Your Throttle. Reads the same GET /stats/data feed (repos ride on it) through client-core.
            { path: "/repos", element: <Repos /> },
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
  { basename: "/mobile" }
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
