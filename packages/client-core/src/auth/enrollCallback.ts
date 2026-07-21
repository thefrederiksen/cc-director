// The core of the device-enrollment callback, extracted from the DeviceCallback screen so the runtime
// DISPATCH - anti-forgery verification, credential classification, and which enroll request is sent -
// is testable without a DOM (client-core carries no jsdom/testing-library). DeviceCallback is now a
// thin consumer: it parses the fragment, calls this, and maps the outcome to a phase / side effect.
//
// Everything here runs BEFORE anything is stored or entered. State verification fails CLOSED, and the
// enroll request that is selected is bound solely to the credential the fragment carried - a hosted
// access_token forwards as Authorization: Bearer with no device_key in the body, a self-host
// device_key posts in the body (the pre-hosted behavior).
import type { EnrollmentShellProfile } from "./enrollRequest";
import { readEnrollCredential, takeEnrollState } from "./enrollRequest";
import { enrollDevice, enrollDeviceHosted } from "../api/enroll";

/**
 * The result of processing a device-enrollment callback fragment. Everything except "enrolled" is a
 * terminal display state the screen renders; "enrolled" carries the local device key the screen then
 * stores, mirrors into the cookie, and lands on the app with. An enroll failure is NOT modelled here:
 * it throws a GatewayError out of runEnrollmentCallback, which the screen catches (so it can tell the
 * Gateway-not-signed-in conflict apart from a genuine error).
 */
export type EnrollCallbackOutcome =
  | { kind: "denied" }
  | { kind: "unverified" }
  | { kind: "noCredential" }
  | { kind: "enrolled"; localKey: string };

/**
 * Process the callback fragment and, when everything checks out, enroll this device.
 *
 * Order is deliberate and every gate runs BEFORE either enroll call:
 *   0. CONSUME THE NONCE FIRST, ON EVERY PATH: the saved anti-forgery state is taken-and-removed
 *      (takeEnrollState) before ANY branch below - denied, unverified, noCredential, or enroll. A
 *      one-time nonce must be spent by whichever callback outcome reaches it first, or it is not
 *      one-time. If the "denied"/error branch returned before consuming it, an attacker who knows a
 *      stale state could send `error=access_denied&state=S` (leaves S live) then
 *      `access_token=theirs&state=S` and REPLAY the still-live nonce to enroll. Taking it up front
 *      makes S dead the instant the first callback (of any outcome) sees it.
 *   1. An explicit `error` in the fragment (the person declined at devthrottle.com) -> "denied".
 *   2. ANTI-FORGERY, FAIL CLOSED: proceed ONLY when we still held the exact nonce we minted before
 *      leaving (consumed in step 0) AND the site echoed that same nonce back in `state`. A missing
 *      saved state (storage unavailable), a missing returned state, or a mismatch all mean we cannot
 *      prove this callback answers a sign-in THIS browser started, so we reject ("unverified") rather
 *      than enroll. Both the self-host and hosted branches are held to this same bar: the website
 *      echoes `state` for both callback shapes and SignIn always mints it, so a legitimate callback of
 *      either kind always carries a matching state. This closes the hosted login-CSRF path where an
 *      attacker delivers a callback carrying THEIR OWN account access token and enrolls a victim into
 *      the attacker's tenant.
 *   3. A credential must be present and unambiguous (readEnrollCredential) -> otherwise "noCredential".
 *   4. Enroll with the request bound to the credential kind.
 *
 * @throws GatewayError when the enroll request itself fails (non-2xx); the caller distinguishes the
 *   409 Gateway-not-signed-in conflict from a genuine error.
 */
export async function runEnrollmentCallback(
  params: URLSearchParams,
  profile: EnrollmentShellProfile,
  deviceId: string,
  signal?: AbortSignal,
): Promise<EnrollCallbackOutcome> {
  // Spend the one-time nonce FIRST, before any branch - so a declined/error callback (or any other
  // outcome) cannot leave it live in storage for a later callback to replay. See step 0 above.
  const expectedState = takeEnrollState();

  if (params.get("error")) {
    return { kind: "denied" };
  }

  const returnedState = params.get("state");
  if (!expectedState || !returnedState || returnedState !== expectedState) {
    return { kind: "unverified" };
  }

  const credential = readEnrollCredential(params);
  if (!credential) {
    return { kind: "noCredential" };
  }

  // The ONLY difference between the two gateway kinds is this request. Hosted forwards the account
  // access token as Authorization: Bearer with no device_key in the body (tenant isolation is bound
  // at the mint from the verified token); self-host posts the device_key in the body, unchanged.
  const localKey =
    credential.mode === "hosted"
      ? await enrollDeviceHosted(credential.accessToken, deviceId, profile.deviceName(), profile.platform(), signal)
      : await enrollDevice(credential.deviceKey, deviceId, profile.deviceName(), profile.platform(), signal);

  return { kind: "enrolled", localKey };
}
