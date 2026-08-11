// The mobile app's address on this Gateway, and the scannable code for it (devthrottle_internal #1508).
//
// Both live in client-core rather than in the Cockpit because they describe the PRODUCT's shape - where
// the mobile app is served - not one shell's layout, and the mobile app itself may one day want to show
// the same code to hand the app to a second phone.
import { authHeaders, GatewayError } from "../api/client";

/** The path the Gateway serves the mobile app at. The old /m mount is 301-redirected to it. */
export const MOBILE_APP_PATH = "/mobile";

/**
 * The absolute address of the mobile app on the Gateway this browser is talking to. Built from the
 * browser's OWN origin, which is the address the person reached the Cockpit on and therefore the one
 * their phone has to reach too - never a configured hostname that may name a different route in.
 */
export function mobileAppUrl(): string {
  return `${window.location.origin}${MOBILE_APP_PATH}`;
}

/**
 * The scannable code for that address, rendered by the Gateway (GET /account/mobile-qr.png).
 *
 * @throws GatewayError carrying the Gateway's own sentence. The case that actually happens is a Cockpit
 *   opened on localhost: a code encoding an address only this machine can reach would scan perfectly and
 *   then time out on the phone, so the Gateway refuses to render one and says which address it saw. The
 *   page shows that sentence instead of a code that cannot work.
 */
export async function getMobileQrPng(signal?: AbortSignal): Promise<Blob> {
  const res = await fetch(`/account/mobile-qr.png`, {
    method: "GET",
    headers: { ...authHeaders() },
    signal,
  });
  if (!res.ok) {
    let detail = `${res.status}`;
    try {
      const body = (await res.json()) as { error?: string };
      if (body?.error) detail = body.error;
    } catch {
      /* not a JSON body - keep the status code */
    }
    throw new GatewayError(res.status, detail);
  }
  return res.blob();
}
