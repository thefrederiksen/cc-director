// The enrollment call (issue #908, shared by the desktop Cockpit since issue #1088): hand the Gateway
// the per-device key this device received from devthrottle.com, and receive back the LOCAL device key
// the Gateway issues and validates offline. This is the only place the cloud-issued key is used; from
// then on the app authenticates with the local key returned here. POST /m/enroll is under /m/, so it
// is reachable before the device holds any credential (it carries its own authorization: the
// account-scoped device key in the body). Both shells enroll through this ONE generalized endpoint -
// the platform field tells the Gateway what kind of device this is (android/ios -> phone, anything
// else, for example "browser" -> browser).
import { GatewayError } from "./client";

/**
 * The Gateway answers POST /m/enroll with 409 when THE GATEWAY ITSELF is not signed in to a DevThrottle
 * account, so it has no account to enroll this device onto.
 *
 * This is not really a failure - on a fresh install it is the expected state - and it is the one
 * enrollment outcome the person can fix themselves, by signing the Gateway in. Callers must therefore be
 * able to tell it apart from a genuine error (403 wrong account, 502 cloud unreachable) and offer that
 * action instead of a "try again" that would fail identically.
 */
export const GATEWAY_NOT_SIGNED_IN = 409;

/**
 * Whether an enrollment failure is "the GATEWAY is not signed in" (as opposed to the person not being
 * signed in, which is a different thing entirely - see GATEWAY_NOT_SIGNED_IN).
 */
export function isGatewayNotSignedIn(err: unknown): err is GatewayError {
  return err instanceof GatewayError && err.status === GATEWAY_NOT_SIGNED_IN;
}

/**
 * Exchange the cloud device key for a local Gateway device key.
 * @returns the local per-device key the app must store and send as its Bearer.
 * @throws GatewayError on any non-2xx, carrying the server's reason (403 = not on this Gateway's
 *   account; 409 = the Gateway is not signed in; 502 = the cloud could not be reached).
 */
export async function enrollDevice(
  cloudDeviceKey: string,
  deviceId: string,
  name: string,
  platform: string,
  signal?: AbortSignal,
): Promise<string> {
  const res = await fetch("/m/enroll", {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json" },
    body: JSON.stringify({ deviceKey: cloudDeviceKey, deviceId, name, platform }),
    signal,
  });
  if (!res.ok) {
    let detail = `${res.status}`;
    try {
      const err = (await res.json()) as { error?: string };
      detail = err.error ?? detail;
    } catch {
      /* non-JSON error body - keep the status code */
    }
    throw new GatewayError(res.status, detail);
  }
  const body = (await res.json()) as { deviceKey?: string };
  const localKey = (body.deviceKey ?? "").trim();
  if (!localKey) {
    throw new GatewayError(res.status, "Enrollment succeeded but returned no device key.");
  }
  return localKey;
}
