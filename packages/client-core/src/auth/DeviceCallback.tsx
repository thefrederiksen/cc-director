// The device-enrollment callback (issue #908), served at /m/device-callback. devthrottle.com redirects
// the phone here after sign-in with the per-device key in the URL FRAGMENT (never the query, so it is
// not sent to any server). This screen reads that key, exchanges it at the Gateway (POST /m/enroll) for
// a LOCAL device key, stores the local key, and enters the app. The account session never reaches here.
import { useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { getInstallId, setDeviceKey } from "./deviceKey";
import { takeEnrollState, detectPlatform, deviceName } from "./enrollRequest";
import { enrollDevice } from "../api/enroll";

type Phase = "working" | "denied" | "error";

export function DeviceCallback() {
  const navigate = useNavigate();
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
        const localKey = await enrollDevice(deviceKey, getInstallId(), deviceName(), detectPlatform());
        if (cancelled) return;
        setDeviceKey(localKey);
        // Strip the key from the URL before entering the app, then go to the roster.
        window.history.replaceState(null, "", "/m/");
        navigate("/", { replace: true });
      } catch (err) {
        if (cancelled) return;
        setPhase("error");
        setMessage(err instanceof Error ? err.message : "Could not connect this device. Please try again.");
      }
    })();

    return () => { cancelled = true; };
  }, [navigate]);

  const container = { maxWidth: 420, margin: "0 auto", padding: "2.5rem 1.25rem", textAlign: "center" as const };

  if (phase === "working") {
    return (
      <div style={container}>
        <h1>Connecting…</h1>
        <p style={{ opacity: 0.8 }}>Finishing sign-in and connecting this phone to your account.</p>
      </div>
    );
  }

  return (
    <div style={container}>
      <h1>{phase === "denied" ? "Sign-in declined" : "Something went wrong"}</h1>
      <p style={{ opacity: 0.8, marginBottom: "1.5rem" }}>{message}</p>
      <button
        type="button"
        onClick={() => navigate("/signin", { replace: true })}
        style={{ padding: "0.8rem 1.25rem", fontSize: "1rem", fontWeight: 600, borderRadius: 10, border: "none", cursor: "pointer" }}
      >
        Try again
      </button>
    </div>
  );
}
