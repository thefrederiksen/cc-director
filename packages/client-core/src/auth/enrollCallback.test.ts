import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { runEnrollmentCallback } from "./enrollCallback";
import {
  COCKPIT_ENROLLMENT_PROFILE,
  MOBILE_ENROLLMENT_PROFILE,
  newEnrollState,
} from "./enrollRequest";

// This drives the ACTUAL enrollment dispatch the DeviceCallback screen runs (runEnrollmentCallback),
// not the request helpers in isolation. It pins the wiring the inspection flagged as untested: that a
// HOSTED fragment reaches the hosted request (Authorization: Bearer, body {deviceId,name,platform},
// NO device_key) and a SELF-HOST fragment reaches the device-key body request. Reverting the hosted
// arm to the old enrollDevice body shape MUST redden the hosted assertion below.
//
// It also pins the fail-closed anti-forgery gate (finding 1 / login-CSRF): a callback whose saved
// anti-forgery state is missing, absent from the fragment, or mismatched must NOT enroll - fetch is
// never called and the outcome is "unverified".

// A minimal in-memory sessionStorage so takeEnrollState/newEnrollState run under the node test
// environment (client-core has no jsdom; the helpers only need getItem/setItem/removeItem).
function stubSessionStorage(): void {
  const store = new Map<string, string>();
  vi.stubGlobal("sessionStorage", {
    getItem: (key: string) => store.get(key) ?? null,
    setItem: (key: string, value: string) => { store.set(key, value); },
    removeItem: (key: string) => { store.delete(key); },
  });
}

// A minimal fetch Response stand-in with a JSON body, enough for the enroll response reader.
function jsonResponse(status: number, body: unknown): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  } as unknown as Response;
}

describe("runEnrollmentCallback dispatch", () => {
  const realFetch = globalThis.fetch;
  beforeEach(() => stubSessionStorage());
  afterEach(() => {
    globalThis.fetch = realFetch;
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it("routes a HOSTED fragment to the hosted request: Bearer + body {deviceId,name,platform}, no device_key", async () => {
    const fetchMock = vi.fn(async () => jsonResponse(200, { deviceKey: "tenant-scoped-key" }));
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    const state = newEnrollState();
    const params = new URLSearchParams(`access_token=access-token-abc&state=${state}`);

    const outcome = await runEnrollmentCallback(params, COCKPIT_ENROLLMENT_PROFILE, "install-9");

    expect(outcome).toEqual({ kind: "enrolled", localKey: "tenant-scoped-key" });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/m/enroll");
    const headers = init.headers as Record<string, string>;
    // The hosted arm MUST use enrollDeviceHosted. Reverting it to enrollDevice (the old shape) drops
    // the Authorization header and puts the token in the body, failing every assertion below.
    expect(headers.Authorization).toBe("Bearer access-token-abc");
    const body = JSON.parse(init.body as string) as Record<string, unknown>;
    expect(body).toEqual({ deviceId: "install-9", name: COCKPIT_ENROLLMENT_PROFILE.deviceName(), platform: "browser" });
    expect("deviceKey" in body).toBe(false);
    expect(JSON.stringify(body)).not.toContain("access-token-abc");
  });

  it("routes a SELF-HOST fragment to the device-key body request with NO Authorization header", async () => {
    const fetchMock = vi.fn(async () => jsonResponse(200, { deviceKey: "local-key" }));
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    const state = newEnrollState();
    const params = new URLSearchParams(`device_key=dk-123&state=${state}`);

    const outcome = await runEnrollmentCallback(params, MOBILE_ENROLLMENT_PROFILE, "install-1");

    expect(outcome).toEqual({ kind: "enrolled", localKey: "local-key" });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/m/enroll");
    const headers = init.headers as Record<string, string>;
    expect(headers.Authorization).toBeUndefined();
    const body = JSON.parse(init.body as string) as Record<string, unknown>;
    expect(body).toEqual({
      deviceKey: "dk-123",
      deviceId: "install-1",
      name: MOBILE_ENROLLMENT_PROFILE.deviceName(),
      platform: MOBILE_ENROLLMENT_PROFILE.platform(),
    });
  });

  it("does NOT enroll a HOSTED callback when there is no saved anti-forgery state (login-CSRF, finding 1)", async () => {
    const fetchMock = vi.fn(async () => jsonResponse(200, { deviceKey: "attacker-tenant-key" }));
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    // No newEnrollState() - nothing was minted before this callback, so no saved state exists. The
    // fragment carries the attacker's access token and any state value; it must be rejected.
    const params = new URLSearchParams("access_token=attacker-token&state=whatever");

    const outcome = await runEnrollmentCallback(params, COCKPIT_ENROLLMENT_PROFILE, "install-9");

    expect(outcome).toEqual({ kind: "unverified" });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("does NOT enroll when the returned state does not match the saved state", async () => {
    const fetchMock = vi.fn(async () => jsonResponse(200, { deviceKey: "k" }));
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    newEnrollState(); // saves a random nonce; the fragment returns a different one
    const params = new URLSearchParams("access_token=tok&state=not-the-saved-one");

    const outcome = await runEnrollmentCallback(params, COCKPIT_ENROLLMENT_PROFILE, "install-9");

    expect(outcome).toEqual({ kind: "unverified" });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("does NOT enroll when the fragment returns no state at all", async () => {
    const fetchMock = vi.fn(async () => jsonResponse(200, { deviceKey: "k" }));
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    newEnrollState();
    const params = new URLSearchParams("access_token=tok"); // saved state exists, none echoed back

    const outcome = await runEnrollmentCallback(params, COCKPIT_ENROLLMENT_PROFILE, "install-9");

    expect(outcome).toEqual({ kind: "unverified" });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("reports 'denied' without enrolling when the fragment carries an error", async () => {
    const fetchMock = vi.fn(async () => jsonResponse(200, { deviceKey: "k" }));
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    const params = new URLSearchParams("error=access_denied");

    const outcome = await runEnrollmentCallback(params, MOBILE_ENROLLMENT_PROFILE, "install-1");

    expect(outcome).toEqual({ kind: "denied" });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("reports 'noCredential' without enrolling when a verified fragment carries no credential", async () => {
    const fetchMock = vi.fn(async () => jsonResponse(200, { deviceKey: "k" }));
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    const state = newEnrollState();
    const params = new URLSearchParams(`state=${state}`);

    const outcome = await runEnrollmentCallback(params, MOBILE_ENROLLMENT_PROFILE, "install-1");

    expect(outcome).toEqual({ kind: "noCredential" });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("reports 'noCredential' for an ambiguous verified fragment carrying BOTH credentials (finding 3)", async () => {
    const fetchMock = vi.fn(async () => jsonResponse(200, { deviceKey: "k" }));
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    const state = newEnrollState();
    const params = new URLSearchParams(`access_token=tok&device_key=dk-123&state=${state}`);

    const outcome = await runEnrollmentCallback(params, COCKPIT_ENROLLMENT_PROFILE, "install-9");

    expect(outcome).toEqual({ kind: "noCredential" });
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
