// Capture-health diagnostics for the browser dictation paths (issue #863).
//
// Audio loss is the same failure on every surface - some of what the user said never reaches the
// transcription model - but it is MEASURED differently per capture technology. The desktop NAudio
// path compares captured bytes to a fixed PCM rate; a browser MediaRecorder produces variable-
// bitrate compressed audio, so bytes are not a yardstick. The honest browser measure is RECORDING
// WALL-CLOCK versus DECODED AUDIO DURATION: record 52 seconds but decode only 38 and ~14 seconds
// was dropped. This module computes that deficit and logs it; it changes no capture behaviour.

export interface BrowserCaptureHealth {
  /** Wall-clock the microphone was open for the segment. */
  recordedMs: number;
  /** Duration of the audio actually decoded from the captured blob. */
  decodedSeconds: number;
  /** Size of the captured (compressed) blob, for reference only. */
  sourceBytes: number;
}

/** Fraction of the recording wall-clock that produced no decoded audio (0 = nothing lost). Clamped
 *  at 0 so codec priming/padding never reads as a negative deficit. */
export function deficitFraction(h: BrowserCaptureHealth): number {
  if (h.recordedMs <= 0) return 0;
  const decodedMs = h.decodedSeconds * 1000;
  return Math.max(0, 1 - decodedMs / h.recordedMs);
}

// A deficit over ~10% of the recording wall-clock is real dropped audio; below that it is normal
// codec priming/padding slack. Shared by the console log and the user-facing warning so the two can
// never disagree about what counts as loss.
const MATERIAL_DEFICIT = 0.1;

/**
 * Human-readable dropped-audio warning for a finished segment, or null when the capture looks
 * healthy. A material deficit means some of what the user said never reached transcription, so the
 * text on screen may be missing words - the user must be TOLD, not just a console line (never fail
 * silently on mobile). The dictation dialog shows this and parks instead of auto-committing.
 */
export function captureLossWarning(h: BrowserCaptureHealth): string | null {
  const deficit = deficitFraction(h);
  if (deficit <= MATERIAL_DEFICIT) return null;
  const missingSeconds = Math.max(1, Math.round((h.recordedMs / 1000) * deficit));
  const unit = missingSeconds === 1 ? "second" : "seconds";
  return (
    `About ${missingSeconds} ${unit} of your audio was not captured (the microphone dropped audio ` +
    "while recording), so words may be missing. Check the text below before you use it."
  );
}

/** Log one capture-health line for a finished segment, tagged by surface so the mobile and Blazor
 *  paths read the same way as the desktop log. A material deficit is a warning, not an error - it is
 *  a measurement, and the audio still ships. */
export function logCaptureHealth(surface: string, h: BrowserCaptureHealth): void {
  const deficit = deficitFraction(h);
  const line =
    `[capture-health] surface=${surface} recordedMs=${h.recordedMs.toFixed(0)} ` +
    `decodedSec=${h.decodedSeconds.toFixed(2)} deficit=${(deficit * 100).toFixed(1)}% ` +
    `sourceBytes=${h.sourceBytes}`;
  if (deficit > MATERIAL_DEFICIT) console.warn(line + " (audio appears to have been dropped before transcription)");
  else console.log(line);
}
