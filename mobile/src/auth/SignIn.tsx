// The mobile Sign in screen (issue #908). Shown by the auth gate whenever the phone has no device
// key. Tapping "Sign in" sends the phone to devthrottle.com's /m-activate page, where the person signs
// in with any provider (Google / GitHub / email) and approves this phone; the site hands the phone's
// per-device key back to /m/device-callback, which enrolls it with the Gateway.
import { getInstallId } from "./deviceKey";
import { SITE_BASE, DEVICE_CALLBACK_PATH, detectPlatform, deviceName, newEnrollState } from "./enrollRequest";

export function SignIn() {
  function start() {
    const installId = getInstallId();
    const state = newEnrollState();
    // The site returns here, to this Gateway's own origin. buildMobileCallbackUrl on the site only ever
    // attaches the device key (never the account session), so this callback carries no account secret.
    const redirectUri = window.location.origin + DEVICE_CALLBACK_PATH;

    const url = new URL("/m-activate", SITE_BASE);
    url.searchParams.set("redirect_uri", redirectUri);
    url.searchParams.set("name", deviceName());
    url.searchParams.set("install_id", installId);
    url.searchParams.set("platform", detectPlatform());
    url.searchParams.set("state", state);

    window.location.assign(url.toString());
  }

  return (
    <div className="signin-screen" style={{ maxWidth: 420, margin: "0 auto", padding: "2rem 1.25rem", textAlign: "center" }}>
      <h1 style={{ marginBottom: "0.5rem" }}>DevThrottle</h1>
      <p style={{ opacity: 0.8, marginBottom: "1.5rem" }}>
        Sign in to connect this phone to your account. You will sign in on devthrottle.com and approve
        this device; it stays signed in until you remove it from your account.
      </p>
      <button
        type="button"
        onClick={start}
        style={{
          display: "block",
          width: "100%",
          padding: "0.9rem 1rem",
          fontSize: "1rem",
          fontWeight: 600,
          borderRadius: 10,
          border: "none",
          cursor: "pointer",
        }}
      >
        Sign in
      </button>
      <p style={{ opacity: 0.6, fontSize: "0.85rem", marginTop: "1.25rem" }}>
        You will be taken to devthrottle.com to sign in, then returned here.
      </p>
    </div>
  );
}
