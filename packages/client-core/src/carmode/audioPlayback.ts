// Car Mode reply playback, extracted from the turn-taking hook so it can be unit-tested on its own AND
// driven by a real-browser audio-event test (the Car Mode "the whole reply was heard" proof).
//
// The bug this file exists to prevent: the perf-round first-sentence split played TWO synthesized chunks
// on ONE reused <audio> element, and on the phone the second chunk's src assignment clobbered the first
// while it was still playing, so the owner heard only the tail of the reply. The correctness rule that
// replaced it: NEVER assign a clip's src to an element that is still playing an earlier clip. playClip
// enforces that per element (one src assignment per call), and the turn machine now plays the whole reply
// as ONE clip, so within a turn there is exactly one src assignment - no clobber is possible.
//
// This module is deliberately free of React and of any Car Mode state, so a plain browser harness can
// import it directly and instrument the <audio> element it plays on.

/** How a single clip's playback ended: it finished on its own, or it was stopped early (a voice/touch
 *  interrupt, or End Car Mode). Both are normal outcomes the turn machine branches on. */
export type PlayOutcome = "ended" | "stopped";

/** The lifecycle stamps a caller wants for diagnostics, fired as the one clip walks its life. Times are
 *  left to the caller (performance.now()); the hooks only mark the transitions, never text. */
export interface PlayClipHooks {
  /** Fired the instant play() has been requested for this clip (the "play-started" diagnostics mark). */
  onPlayStarted?: () => void;
  /** Fired once when the clip is done, with how it ended, so the caller can record completed-vs-cutoff. */
  onPlayEnded?: (outcome: PlayOutcome) => void;
  /** Fired when the element's play() promise REJECTS - on mobile this is the autoplay block (a play() not
   *  tied to a live user gesture is refused with NotAllowedError). Lets the caller record that the reply
   *  never actually sounded (the mobile cut-off bug), distinct from a normal early stop. */
  onPlayRejected?: (error: unknown) => void;
}

/**
 * Play ONE audio clip on the given element and resolve when it FINISHES ("ended") or is stopped early
 * ("stopped"). The element's src is assigned EXACTLY ONCE here; this function never reassigns it, so a clip
 * that is still playing can never be clobbered by this call. Stopping is cooperative: the caller is handed
 * a stop function through <paramref>registerStop</paramref> (typically stored in a ref) and calling it
 * resolves the play promise as "stopped" so the turn loop unwinds cleanly. A play() rejection (for example
 * an autoplay block) also resolves "stopped" so the loop never hangs.
 *
 * @param audio the media element to play on (its src is set here)
 * @param url the object URL of the clip's audio blob
 * @param registerStop called with a function that, when invoked, stops this clip early
 * @param hooks optional lifecycle marks for diagnostics
 */
export function playClip(
  audio: HTMLAudioElement,
  url: string,
  registerStop: (stop: () => void) => void,
  hooks?: PlayClipHooks,
): Promise<PlayOutcome> {
  return new Promise<PlayOutcome>((resolve) => {
    let done = false;
    const finish = (how: PlayOutcome) => {
      if (done) return;
      done = true;
      audio.onended = null;
      audio.onerror = null;
      registerStop(() => {});
      hooks?.onPlayEnded?.(how);
      resolve(how);
    };
    audio.onended = () => finish("ended");
    // An element error (a clip that cannot decode) must not hang the turn: treat it as a stop so the loop
    // unwinds and the microphone returns to the owner (no silent stall).
    audio.onerror = () => finish("stopped");
    registerStop(() => finish("stopped"));
    audio.src = url;
    hooks?.onPlayStarted?.();
    // A play() rejection is the mobile autoplay block (NotAllowedError when the play is not tied to a live
    // user gesture): log the specific reason and mark it so turn diagnostics can show the reply never
    // sounded, then treat it as a stop so the turn loop unwinds and the microphone returns (no silent
    // stall). The unlock-on-Start-gesture (useCarMode) is what prevents this from happening.
    void audio.play().catch((error: unknown) => {
      const name = error instanceof Error ? error.name : "unknown";
      const message = error instanceof Error ? error.message : String(error);
      console.log(`[CarMode] reply audio play() rejected: ${name}: ${message}`);
      hooks?.onPlayRejected?.(error);
      finish("stopped");
    });
  });
}
