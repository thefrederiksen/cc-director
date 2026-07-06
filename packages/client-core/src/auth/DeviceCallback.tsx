// The shared device-enrollment callback (issue #908, generalized for the desktop Cockpit in issue
// #1088), served at the installed profile's callback path (/m/device-callback for the phone,
// /device-callback for the Cockpit). devthrottle.com redirects the browser here after sign-in with the
// per-device key in the URL FRAGMENT (never the query, so it is not sent to any server - issue #1082).
// This screen reads that key, exchanges it at the Gateway (POST /m/enroll) for a LOCAL device key,
// stores the local key through the shared device-key store, mirrors it into the cc-gateway-token
// cookie (so the terminal WebSocket and hard navigations authenticate immediately), and enters the app
// on the originally-requested route. The account session never reaches here.
import { useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { getInstallId, setDeviceKey } from "./deviceKey";
import { enrollmentProfile, takeEnrollState, takeEnrollNext } from "./enrollRequest";
import { enrollDevice } from "../api/enroll";
import { ensureGatewayCookie } from "../api/client";

type Phase = "working" | "denied" | "error";

export function DeviceCallback() {
  const navigate = useNavigate();
  const profile = enrollmentProfile();
  const [phase, setPhase] = useState<Phase>("working");
  const [message, setMessage] = useState("");
  // Guards the enroll to exactly once (survives React StrictMode's dev double-mount).
  const started = useRef(false);

  useEffect(() => {
    if (started.current) return;
    started.current = true;

    let cancelled = false;
    (async () => {
      const rawHash = window.location.hash.startsWith("#") ? window.location.hash.slice(1) : window.location.hash;
      const params = new URLSearchParams(rawHash);
      const error = params.get("error");
      const deviceKey = params.get("device_key");
      const state = params.get("state");
      const expected = takeEnrollState();

      if (error) {
        setPhase("denied");
        setMessage("Sign-in was declined. Nothing was connected.");
        return;
      }
      // Verify the round trip when we have a saved state; a null expected-state (storage cleared) is not
      // treated as a failure, so a legitimate sign-in is never blocked by missing session storage.
      if (expected && state !== expected) {
        setPhase("error");
        setMessage("This sign-in could not be verified. Please try again.");
        return;
      }
      if (!deviceKey) {
        setPhase("error");
        setMessage("Sign-in did not return a device key. Please try again.");
        return;
      }

      try {
        const localKey = await enrollDevice(deviceKey, getInstallId(), profile.deviceName(), profile.platform());
        if (cancelled) return;
        setDeviceKey(localKey);
        // Mirror the fresh key into the cc-gateway-token cookie right away, so the terminal WebSocket
        // and any hard navigation authenticate without waiting for the next full page load.
        ensureGatewayCookie();
        // Land on the originally-requested route when one was remembered (issue #1088), otherwise the
        // shell's default. Strip the key from the URL/history first, then hand the route to the router.
        const landing = takeEnrollNext() ?? profile.defaultLanding;
        window.history.replaceState(null, "", profile.basename + landing);
        navigate(landing, { replace: true });
      } catch (err) {
        if (cancelled) return;
        setPhase("error");
        setMessage(err instanceof Error ? err.message : "Could not connect this device. Please try again.");
      }
    })();

    return () => { cancelled = true; };
    // The profile is module-level configuration installed once at shell startup, not reactive state.
  }, [navigate, profile]);

  const container = { maxWidth: 420, margin: "0 auto", padding: "2.5rem 1.25rem", textAlign: "center" as const };

  if (phase === "working") {
    return (
      <div style={container}>
        <h1>Connecting…</h1>
        <p style={{ opacity: 0.8 }}>Finishing sign-in and connecting this {profile.deviceLabel} to your account.</p>
      </div>
    );
  }

  return (
    <div style={container}>
      <h1>{phase === "denied" ? "Sign-in declined" : "Something went wrong"}</h1>
      <p style={{ opacity: 0.8, marginBottom: "1.5rem" }}>{message}</p>
      <button
        type="button"
        onClick={() => navigate(profile.signInPath, { replace: true })}
        style={{ padding: "0.8rem 1.25rem", fontSize: "1rem", fontWeight: 600, borderRadius: 10, border: "none", cursor: "pointer" }}
      >
        Try again
      </button>
    </div>
  );
}
