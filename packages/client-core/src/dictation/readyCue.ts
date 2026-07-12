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

/**
 * Start the Car Mode "thinking" ambient cue and return a function that stops it. Played WHILE the brain
 * works (after the owner taps "Over and out"), so the ~2s of transcribe -> brain -> text-to-speech is not
 * dead silence - the owner hears, gently, that his turn was taken and is being worked. It reuses the
 * dictation water-droplet character (playReadyCue) but SOFT and LOW-VOLUME (Grok-style gentle, peak ~0.07
 * vs the ack cue's 0.5), repeating slowly, so it is an unobtrusive "working" texture, NOT the sharp ack
 * "plink". The caller stops it the instant the reply audio starts (see useCarMode speakAndPlay). Same
 * best-effort contract as the other cues - it is synthesized with the Web Audio API (no bundled asset) and
 * never throws; if audio output is unavailable the returned stop function is a harmless no-op.
 *
 * It MUST be started from within a user gesture (the "Over and out" tap) so mobile lets its AudioContext
 * run; a context created past the gesture would stay suspended and silent.
 */
export function startThinkingCue(): () => void {
  try {
    const AudioCtor =
      window.AudioContext || (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!AudioCtor) return () => {};

    const ctx = new AudioCtor();
    if (ctx.state === "suspended") void ctx.resume();

    let stopped = false;

    // One soft droplet: the same rising-glide plink as the ack cue but at a fraction of the volume, so it
    // reads as a calm, distant "working" tone rather than a foreground beep.
    const drop = () => {
      if (stopped || ctx.state === "closed") return;
      const now = ctx.currentTime;
      const duration = 0.18;

      const osc = ctx.createOscillator();
      const gain = ctx.createGain();
      osc.type = "sine";
      osc.frequency.setValueAtTime(300, now);
      osc.frequency.exponentialRampToValueAtTime(640, now + duration * 0.9);

      gain.gain.setValueAtTime(0.0001, now);
      // Peak ~0.07 - deliberately gentle (the ack cue peaks at 0.5). Low enough to sit under the room, so
      // it never competes with the reply that follows.
      gain.gain.exponentialRampToValueAtTime(0.07, now + 0.02);
      gain.gain.exponentialRampToValueAtTime(0.0001, now + duration);

      osc.connect(gain).connect(ctx.destination);
      osc.start(now);
      osc.stop(now + duration + 0.02);
    };

    drop();
    // A slow, unhurried repeat (~1.4s) so it is an ambient pulse, not a rapid alarm.
    const timer = window.setInterval(drop, 1400);

    return () => {
      if (stopped) return;
      stopped = true;
      window.clearInterval(timer);
      try {
        void ctx.close();
      } catch {
        // context already closed; nothing to do
      }
    };
  } catch {
    // Best-effort courtesy cue; if audio output is unavailable, hand back a no-op stopper.
    return () => {};
  }
}

/**
 * Play the Car Mode "your turn" cue once - the second, deliberately DIFFERENT turn-boundary tone
 * (Car Mode mission, "The audible handshake"). It fires when the assistant finishes speaking (or is
 * interrupted) and the microphone is live again for the owner, so - eyes-free, phone in pocket - he
 * can tell whose turn it is by sound alone. It must be unmistakably distinct from the rising water-drop
 * "my turn" cue (playReadyCue): this is a LOWER, FALLING two-pulse tone on a square wave, so it differs
 * in pitch direction, rhythm, and timbre all at once. Same best-effort contract as playReadyCue - it is
 * synthesized with the Web Audio API (no bundled asset) and never throws; an unavailable audio output is
 * skipped silently.
 */
export function playYourTurnCue(): void {
  try {
    const AudioCtor =
      window.AudioContext || (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!AudioCtor) return;

    const ctx = new AudioCtor();
    if (ctx.state === "suspended") void ctx.resume();

    const now = ctx.currentTime;

    // Two short pulses (a "your turn, go ahead" double-blip). The rhythm alone already separates it
    // from the single water-drop; the pitch and timbre below make it unambiguous.
    const pulse = 0.09;
    const gap = 0.06;
    const total = pulse + gap + pulse;

    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    // A square wave reads as a flatter, "electronic" blip - clearly not the pure-sine droplet plink.
    osc.type = "square";

    // Pitch steps DOWN across the two pulses (falling), the opposite of the water-drop's rising glide.
    osc.frequency.setValueAtTime(660, now);
    osc.frequency.setValueAtTime(440, now + pulse + gap);

    // Gate the amplitude into two clean pulses with a silent gap between them.
    gain.gain.setValueAtTime(0.0001, now);
    gain.gain.exponentialRampToValueAtTime(0.4, now + 0.01);
    gain.gain.setValueAtTime(0.4, now + pulse - 0.01);
    gain.gain.exponentialRampToValueAtTime(0.0001, now + pulse);
    gain.gain.setValueAtTime(0.0001, now + pulse + gap);
    gain.gain.exponentialRampToValueAtTime(0.4, now + pulse + gap + 0.01);
    gain.gain.setValueAtTime(0.4, now + total - 0.01);
    gain.gain.exponentialRampToValueAtTime(0.0001, now + total);

    osc.connect(gain).connect(ctx.destination);
    osc.start(now);
    osc.stop(now + total + 0.02);
    osc.onended = () => void ctx.close();
  } catch {
    // Best-effort courtesy cue; never disrupt the turn if audio output is unavailable.
  }
}
