// Holding the screen awake, which is what turns a phone on a charger into a kitchen appliance.
//
// A browser is only allowed to hold the microphone while its page is in the foreground. On a charging
// stand that is a fine restriction, so long as the screen never goes to sleep. The browser gives us
// exactly one way to ask for that, and it hands the lock back whenever the page is hidden, so it has
// to be taken again every time the page returns.

export interface ScreenWakeLock {
  release(): Promise<void>;
  readonly isHeld: boolean;
}

export class ScreenWakeLockUnavailableError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "ScreenWakeLockUnavailableError";
  }
}

interface WakeLockSentinelLike {
  released: boolean;
  release(): Promise<void>;
  addEventListener(type: "release", listener: () => void): void;
}
interface WakeLockLike {
  request(type: "screen"): Promise<WakeLockSentinelLike>;
}

/**
 * Keep the screen on until release is called.
 *
 * Re-takes the lock when the page becomes visible again, because the browser drops it every time the
 * page is hidden and never gives it back on its own.
 */
export async function holdScreenAwake(onNotice: (message: string) => void): Promise<ScreenWakeLock> {
  const foundWakeLock = (navigator as unknown as { wakeLock?: WakeLockLike }).wakeLock;
  if (foundWakeLock === undefined) {
    throw new ScreenWakeLockUnavailableError(
      "This browser cannot keep the screen awake. On iPhone that needs iOS 18.4 or later, and the page has to be installed to the home screen.",
    );
  }
  const wakeLock: WakeLockLike = foundWakeLock;

  let sentinel: WakeLockSentinelLike | null = null;
  let released = false;

  async function take(): Promise<void> {
    if (released) {
      return;
    }
    try {
      sentinel = await wakeLock.request("screen");
      sentinel.addEventListener("release", () => {
        if (!released) {
          onNotice("The screen wake lock was released by the browser. It will be taken again when this page is visible.");
        }
      });
    } catch (error) {
      onNotice(
        `The screen wake lock could not be taken: ${error instanceof Error ? error.message : String(error)}`,
      );
    }
  }

  async function onVisibilityChange(): Promise<void> {
    if (document.visibilityState === "visible" && !released) {
      await take();
    }
  }

  document.addEventListener("visibilitychange", onVisibilityChange);
  await take();

  return {
    async release() {
      released = true;
      document.removeEventListener("visibilitychange", onVisibilityChange);
      if (sentinel !== null && !sentinel.released) {
        await sentinel.release();
      }
      sentinel = null;
    },
    get isHeld() {
      return sentinel !== null && !sentinel.released;
    },
  };
}
