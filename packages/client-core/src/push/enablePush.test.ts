import { afterEach, describe, expect, it, vi } from "vitest";
import { enablePush } from "./register";

// EVERY WAY THE "ENABLE NOTIFICATIONS" TAP CAN END HAS A NAME AND A SENTENCE.
//
// These tests exist because two of those endings used to be invisible. The banner awaited enablePush(),
// swallowed failures into console.warn, and then re-read Notification.permission to decide what to draw:
//   - permission stayed "default" (no prompt shown) -> the banner re-rendered identically, so the button
//     appeared to do nothing at all;
//   - permission was granted but registering the device failed -> the banner DISAPPEARED as if it had
//     worked, while no push could ever arrive.
// So the assertions below are about the OUTCOME BEING REPORTED, not just about the happy path working.
//
// This file runs in Node, so each test builds the exact browser surface enablePush() reads. That is the
// point: the branches turn on APIs a Node test does not have, so a test that did not fake them would be
// testing nothing.

type Stubs = {
  permission?: NotificationPermission;
  requestPermission?: () => Promise<NotificationPermission>;
  displayMode?: string;
  serviceWorkerReady?: Promise<unknown>;
  existingSubscription?: unknown;
  subscribe?: () => Promise<unknown>;
  fetch?: typeof fetch;
};

// vi.stubGlobal, not plain assignment: in current Node `navigator` is a getter-only global, so
// assigning to it throws. Unstubbed wholesale in afterEach.
const stubGlobal = (name: string, value: unknown): void => {
  vi.stubGlobal(name, value);
};

// Build a browser that CAN do Web Push (pushSupported() reads navigator.serviceWorker, window.PushManager
// and window.Notification), then let each test bend one piece of it.
function installBrowser(stubs: Stubs): void {
  const registration = {
    pushManager: {
      getSubscription: () => Promise.resolve(stubs.existingSubscription ?? null),
      subscribe: stubs.subscribe ?? (() => Promise.resolve(subscriptionLike())),
    },
  };

  const nav = {
    serviceWorker: {
      ready: stubs.serviceWorkerReady ?? Promise.resolve(registration),
    },
  };

  const notification = {
    permission: stubs.permission ?? "default",
    requestPermission: stubs.requestPermission ?? (() => Promise.resolve(stubs.permission ?? "default")),
  };

  const win = {
    PushManager: function PushManager() {},
    Notification: notification,
    matchMedia: (query: string) => ({
      matches: stubs.displayMode !== undefined && query.includes(stubs.displayMode),
    }),
  };

  stubGlobal("navigator", nav);
  stubGlobal("window", win);
  stubGlobal("Notification", notification);
  stubGlobal("fetch", stubs.fetch ?? okFetch());
  stubGlobal("atob", (s: string) => Buffer.from(s, "base64").toString("binary"));
}

function subscriptionLike() {
  return {
    endpoint: "https://push.example/abc",
    toJSON: () => ({ endpoint: "https://push.example/abc", keys: { p256dh: "p", auth: "a" } }),
  };
}

// A Gateway that hands out a key and accepts the subscription.
function okFetch(): typeof fetch {
  return ((url: string) => {
    if (String(url).includes("vapid-public-key"))
      return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({ publicKey: "B".repeat(87) }) });
    return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({ ok: true }) });
  }) as unknown as typeof fetch;
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe("enablePush reports what happened", () => {
  it("says unsupported when the browser cannot do Web Push at all", async () => {
    // No browser APIs installed - the bare Node surface.
    const result = await enablePush();
    expect(result.state).toBe("unsupported");
    expect(result.message.length).toBeGreaterThan(0);
  });

  it("calls a standing block 'blocked' and points at browser settings", async () => {
    installBrowser({ requestPermission: () => Promise.resolve("denied") });
    const result = await enablePush();
    expect(result.state).toBe("blocked");
    expect(result.message).toMatch(/site settings/i);
  });

  it("calls an unanswered prompt 'dismissed' - NOT blocked, and never silence", async () => {
    // The invisible case: the browser resolves "default" because it showed no prompt (Chrome's quiet
    // permission user interface) or the user waved it away. Permission is unchanged, which is exactly why
    // the old banner had nothing to re-render and looked broken.
    installBrowser({ requestPermission: () => Promise.resolve("default"), displayMode: "standalone" });
    const result = await enablePush();
    expect(result.state).toBe("dismissed");
    expect(result.message).toMatch(/dismissed/i);
  });

  it("blames the in-app browser window when the page is not the installed app", async () => {
    // Same "default" outcome, different cause: a window that is not the installed app cannot be granted
    // notification permission on Android, so the message must name that and say what to do instead.
    installBrowser({ requestPermission: () => Promise.resolve("default") }); // no display-mode matches
    const result = await enablePush();
    expect(result.state).toBe("dismissed");
    expect(result.message).toMatch(/home screen/i);
  });

  it("reports a failure when permission is granted but no service worker ever takes control", async () => {
    // navigator.serviceWorker.ready NEVER resolves and NEVER rejects when nothing is registered for this
    // scope. Awaiting it bare left the button on "Enabling..." for ever - the hang this bound converts
    // into a stated failure.
    vi.useFakeTimers();
    installBrowser({
      requestPermission: () => Promise.resolve("granted"),
      serviceWorkerReady: new Promise(() => {
        /* never settles, exactly like a scope with no registration */
      }),
    });
    const pending = enablePush();
    await vi.advanceTimersByTimeAsync(9000);
    const result = await pending;
    expect(result.state).toBe("failed");
    expect(result.message).toMatch(/service worker/i);
  });

  it("reports a failure when the Gateway refuses the subscription, instead of looking like success", async () => {
    installBrowser({
      requestPermission: () => Promise.resolve("granted"),
      fetch: ((url: string) => {
        if (String(url).includes("vapid-public-key"))
          return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({ publicKey: "B".repeat(87) }) });
        return Promise.resolve({ ok: false, status: 500, json: () => Promise.resolve({}) });
      }) as unknown as typeof fetch,
    });
    const result = await enablePush();
    expect(result.state).toBe("failed");
    expect(result.message).toMatch(/500/);
  });

  it("reports a failure when the Gateway hands back an empty key", async () => {
    installBrowser({
      requestPermission: () => Promise.resolve("granted"),
      fetch: (() =>
        Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({ publicKey: "  " }) })) as unknown as typeof fetch,
    });
    const result = await enablePush();
    expect(result.state).toBe("failed");
    expect(result.message).toMatch(/empty VAPID public key/i);
  });

  it("says granted once the device is registered with the Gateway", async () => {
    const posted: string[] = [];
    installBrowser({
      requestPermission: () => Promise.resolve("granted"),
      fetch: ((url: string) => {
        posted.push(String(url));
        if (String(url).includes("vapid-public-key"))
          return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({ publicKey: "B".repeat(87) }) });
        return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({ ok: true }) });
      }) as unknown as typeof fetch,
    });
    const result = await enablePush();
    expect(result.state).toBe("granted");
    expect(posted.some((u) => u.includes("/push/subscribe"))).toBe(true);
  });

  it("never throws - a browser that rejects the permission request comes back as a failure", async () => {
    installBrowser({ requestPermission: () => Promise.reject(new Error("not allowed in this context")) });
    const result = await enablePush();
    expect(result.state).toBe("failed");
    expect(result.message).toMatch(/not allowed in this context/);
  });
});
