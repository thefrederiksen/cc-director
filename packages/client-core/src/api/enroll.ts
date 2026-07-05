// The enrollment call (issue #908): hand the Gateway the per-device key the phone received from
// devthrottle.com, and receive back the LOCAL device key the Gateway issues and validates offline.
// This is the only place the cloud-issued key is used; from then on the app authenticates with the
// local key returned here. POST /m/enroll is under /m/, so it is reachable before the phone holds any
// credential (it carries its own authorization: the account-scoped device key in the body).
import { GatewayError } from "./client";

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
