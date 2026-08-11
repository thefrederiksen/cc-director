// The shared Sign in screen (issue #908, generalized for the desktop Cockpit in issue #1088). Shown
// by a shell's auth gate whenever this device has no device key. Pressing "Sign in" sends the browser
// to devthrottle.com's /m-activate page, where the person signs in with any provider
// (Google / GitHub / email) and approves this device; the site hands the per-device key back - in the
// URL FRAGMENT only (issue #1082) - to this shell's callback route, which enrolls it with the Gateway.
//
// Both shells render this one screen: the phone (auth gate at /mobile/signin) and the desktop Cockpit
// (auth gate + the Gateway's signed-out redirect at /signin). Everything shell-specific - the
// callback path, the platform, the device label - comes from the installed EnrollmentShellProfile.
// The desktop gate carries the originally-requested route in ?next=; it is remembered here so the
// callback can land the browser back on that exact route after the round trip.
//
// This screen is ALSO the "add another account" entry (devthrottle_internal #1509): a browser that
// already holds an account reaches it from the account switcher, and the only difference is the words -
// the round trip is identical, and the account that comes back is APPENDED rather than replacing the
// one already here.
import { listAccounts, newPendingInstallId } from "./accountStore";
import { SITE_BASE, enrollmentProfile, newEnrollState, rememberEnrollNext } from "./enrollRequest";

export function SignIn() {
  const profile = enrollmentProfile();
  // Adding to an existing browser rather than enrolling an empty one. Read at render: the only way to
  // arrive here holding an account is the switcher's "Add account", and the only way to arrive holding
  // none is the auth gate.
  const adding = listAccounts().length > 0;

  function start() {
    // A FRESH install id for every sign-in, minted here and consumed by the callback leg. This is what
    // makes a second account a second DEVICE on the cloud roster instead of a collision with the
    // account already on this browser - see accountStore.
    const installId = newPendingInstallId();
    const state = newEnrollState();

    // Preserve the originally-requested route (?next=) across the round trip (issue #1088). The
    // value is validated to an in-app path; devthrottle.com only ever returns to the fixed callback
    // path, so this is remembered locally beside the anti-forgery state.
    const next = new URLSearchParams(window.location.search).get("next");
    rememberEnrollNext(next);

    // The site returns here, to this Gateway's own origin. buildMobileCallbackUrl on the site only ever
    // attaches the device key (never the account session), so this callback carries no account secret.
    const redirectUri = window.location.origin + profile.callbackPath;

    const url = new URL("/m-activate", SITE_BASE);
    url.searchParams.set("redirect_uri", redirectUri);
    url.searchParams.set("name", profile.deviceName());
    url.searchParams.set("install_id", installId);
    url.searchParams.set("platform", profile.platform());
    url.searchParams.set("state", state);

    window.location.assign(url.toString());
  }

  return (
    <div className="signin-screen" style={{ maxWidth: 420, margin: "0 auto", padding: "2rem 1.25rem", textAlign: "center" }}>
      <h1 style={{ marginBottom: "0.5rem" }}>{adding ? "Add an account" : "DevThrottle"}</h1>
      <p style={{ opacity: 0.8, marginBottom: "1.5rem" }}>
        {adding
          ? `Sign in to the account you want to add. It joins the accounts already on this ${profile.deviceLabel} - nothing signs out, and you can switch between them without signing in again.`
          : `Sign in to connect this ${profile.deviceLabel} to your account. You will sign in on devthrottle.com and approve this device; it stays signed in until you remove it from your account.`}
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
        {adding ? "Sign in to another account" : "Sign in"}
      </button>
      <p style={{ opacity: 0.6, fontSize: "0.85rem", marginTop: "1.25rem" }}>
        You will be taken to devthrottle.com to sign in, then returned here.
      </p>
    </div>
  );
}
