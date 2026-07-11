// The dictation "ready" cue: a short water-drop "bloop" played the instant the microphone is
// confirmed capturing real audio, so the user hears exactly when to start speaking - the same
// courtesy the Windows dictation panel gives with its ready beep, and the web twin of the desktop
// DesktopAudioCue. The tone is SYNTHESIZED with the Web Audio API (no bundled audio files), so it
// sounds the same character as the desktop cue.
//
// Best-effort by design: a cue is a courtesy, so a browser without Web Audio, or an autoplay block,
// must never disrupt the dictation turn - it is skipped silently.

/**
 * Play the dictation "ready" water-drop bloop once. Returns immediately; the sound plays on the
 * Web Audio clock. Never throws. Should be called from within/just after a user gesture (the Speak
 * tap that opened dictation) so the browser permits playback.
 */
export function playReadyCue(): void {
  try {
    const AudioCtor =
      window.AudioContext || (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!AudioCtor) return;

    const ctx = new AudioCtor();
    // A context created outside a gesture can start "suspended"; resume so the cue is audible.
    if (ctx.state === "suspended") void ctx.resume();

    const now = ctx.currentTime;
    const duration = 0.2;

    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.type = "sine";

    // Pitch glides UP over the cue; paired with the fast decay below this is the perceptual
    // signature of a droplet (a "plink") rather than a flat beep. Matches the desktop cue's shape.
    osc.frequency.setValueAtTime(380, now);
    osc.frequency.exponentialRampToValueAtTime(1150, now + duration * 0.9);

    // Fast click-free attack, then an exponential decay to near-silence. exponentialRamp cannot
    // target exactly 0, so ramp to a tiny floor and stop the oscillator right after.
    gain.gain.setValueAtTime(0.0001, now);
    gain.gain.exponentialRampToValueAtTime(0.5, now + 0.01);
    gain.gain.exponentialRampToValueAtTime(0.0001, now + duration);

    osc.connect(gain).connect(ctx.destination);
    osc.start(now);
    osc.stop(now + duration + 0.02);
    osc.onended = () => void ctx.close();
  } catch {
    // Best-effort courtesy cue; never disrupt dictation if audio output is unavailable.
  }
}
