// The shared device-enrollment callback (issue #908, generalized for the desktop Cockpit in issue
// #1088), served at the installed profile's callback path (/m/device-callback for the phone,
// /device-callback for the Cockpit). devthrottle.com redirects the browser here after sign-in with the
// enrollment credential in the URL FRAGMENT (never the query, so it is not sent to any server - issue
// #1082). This screen reads that credential, exchanges it at the Gateway (POST /m/enroll) for a LOCAL
// device key, stores the local key through the shared device-key store, mirrors it into the
// cc-gateway-token cookie (so the terminal WebSocket and hard navigations authenticate immediately),
// and enters the app on the originally-requested route. The account session never reaches here.
//
// Which credential the fragment carries decides the path (multi-tenant hosted sign-in, Phase C): a
// HOSTED gateway round trip returns the account's Supabase access_token (forwarded to the mint as
// Authorization: Bearer), while a SELF-HOST round trip returns a device_key (posted in the body, the
// pre-hosted behavior). readEnrollCredential picks one; only the enroll call differs - the state
// check, cookie mirror, landing, and error handling below are shared byte-for-byte.
import { useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { getInstallId, setDeviceKey } from "./deviceKey";
import { enrollmentProfile, takeEnrollState, takeEnrollNext, readEnrollCredential } from "./enrollRequest";
import { enrollDevice, enrollDeviceHosted, isGatewayNotSignedIn } from "../api/enroll";
import { ensureGatewayCookie } from "../api/client";
import { beginSignIn } from "../account/accountClient";

// "gatewaySignedOut" is the Gateway itself not being signed in to a DevThrottle account (HTTP 409 from
// /m/enroll). It is deliberately NOT an error phase: on a fresh install it is the EXPECTED state - the
// Gateway has to join an account before it can enroll anything onto that account.
//
// It needs its own phase because the generic error phase strands the person. The Gateway's message says
// "sign the Gateway in and try again", but the only action offered was "Try again", which returns to the
// sign-in screen and fails again for exactly the same reason - a loop with no exit. The two sign-ins are
// easy to conflate: the person HAS signed themselves in at devthrottle.com; it is the GATEWAY that has
// not. So this phase says which one is missing and offers the action that fixes it.
type Phase = "working" | "denied" | "error" | "gatewaySignedOut";

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
      const credential = readEnrollCredential(params);
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
      if (!credential) {
        setPhase("error");
        setMessage("Sign-in did not return a device key. Please try again.");
        return;
      }

      try {
        // Only the enroll call differs between the two gateway kinds; everything below is shared. The
        // hosted path forwards the account access_token as Authorization: Bearer with no device_key in
        // the body; the self-host path posts the device_key in the body exactly as before.
        const localKey =
          credential.mode === "hosted"
            ? await enrollDeviceHosted(credential.accessToken, getInstallId(), profile.deviceName(), profile.platform())
            : await enrollDevice(credential.deviceKey, getInstallId(), profile.deviceName(), profile.platform());
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
        if (isGatewayNotSignedIn(err)) {
          setPhase("gatewaySignedOut");
          setMessage(err.message);
          return;
        }
        setPhase("error");
        setMessage(err instanceof Error ? err.message : "Could not connect this device. Please try again.");
      }
    })();

    return () => { cancelled = true; };
    // The profile is module-level configuration installed once at shell startup, not reactive state.
  }, [navigate, profile]);

  const container = { maxWidth: 420, margin: "0 auto", padding: "2.5rem 1.25rem", textAlign: "center" as const };

  const button = {
    padding: "0.8rem 1.25rem", fontSize: "1rem", fontWeight: 600,
    borderRadius: 10, border: "none", cursor: "pointer",
  } as const;

  if (phase === "working") {
    return (
      <div style={container}>
        <h1>Connecting…</h1>
        <p style={{ opacity: 0.8 }}>Finishing sign-in and connecting this {profile.deviceLabel} to your account.</p>
      </div>
    );
  }

  // The Gateway has no account yet. The person is signed in; the GATEWAY is not - so name that
  // difference plainly and give them the one action that resolves it, rather than a "Try again" that
  // would fail identically. beginSignIn navigates to the Gateway's public sign-in front door, which
  // sends this browser to devthrottle.com and hands it back to the Gateway's own callback.
  if (phase === "gatewaySignedOut") {
    return (
      <div style={container}>
        <h1>This Gateway is not signed in yet</h1>
        <p style={{ opacity: 0.8, marginBottom: "1.5rem" }}>{message}</p>
        <button type="button" onClick={() => beginSignIn()} style={button}>
          Sign the Gateway in
        </button>
        <p style={{ opacity: 0.7, marginTop: "1.25rem", fontSize: "0.9rem" }}>
          You are signed in already - it is the Gateway that needs to join your account before it can
          connect this {profile.deviceLabel}. Once it has, sign in here again.
        </p>
      </div>
    );
  }

  return (
    <div style={container}>
      <h1>{phase === "denied" ? "Sign-in declined" : "Something went wrong"}</h1>
      <p style={{ opacity: 0.8, marginBottom: "1.5rem" }}>{message}</p>
      <button type="button" onClick={() => navigate(profile.signInPath, { replace: true })} style={button}>
        Try again
      </button>
    </div>
  );
}
