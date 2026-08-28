// Proof that the microphone is open, in pixels.
//
// The speech recogniser is a black box: it reports words, and when it reports nothing there is no way
// to tell a silent room from a dead microphone from a recogniser that never started. That ambiguity
// is unacceptable in a thing whose entire job is to be listening, so this opens the microphone
// separately and reports the level it is actually seeing.
//
// It is a second consumer of the microphone alongside the recogniser. That is deliberate. If the two
// cannot coexist on some device we need to know that early and visibly, rather than shipping
// something that cannot prove it is awake.

export interface MicLevel {
  stop(): void;
  readonly deviceLabel: string;
  readonly echoCancellation: boolean;
}

/**
 * Watch the microphone and report a level between 0 and 1, roughly sixty times a second.
 *
 * Reports the peak of each frame rather than an average: speech is spiky, and an average over a
 * twentieth of a second reads as a much smaller number than the one a person expects to see when
 * they say a word at their phone.
 */
export async function watchMicLevel(onLevel: (level: number) => void): Promise<MicLevel> {
  const stream = await navigator.mediaDevices.getUserMedia({
    audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true },
    video: false,
  });

  const [track] = stream.getAudioTracks();
  const settings = track?.getSettings() ?? {};

  const context = new AudioContext();
  if (context.state === "suspended") {
    await context.resume();
  }
  const source = context.createMediaStreamSource(stream);
  const analyser = context.createAnalyser();
  analyser.fftSize = 1024;
  analyser.smoothingTimeConstant = 0.1;
  source.connect(analyser);

  const buffer = new Float32Array(analyser.fftSize);
  let running = true;

  function frame(): void {
    if (!running) {
      return;
    }
    analyser.getFloatTimeDomainData(buffer);
    let peak = 0;
    for (let i = 0; i < buffer.length; i += 1) {
      const value = Math.abs(buffer[i]);
      if (value > peak) {
        peak = value;
      }
    }
    onLevel(peak);
    requestAnimationFrame(frame);
  }
  requestAnimationFrame(frame);

  return {
    stop() {
      running = false;
      source.disconnect();
      stream.getTracks().forEach((t) => t.stop());
      void context.close();
    },
    deviceLabel: track?.label && track.label.length > 0 ? track.label : "Unnamed microphone",
    echoCancellation: settings.echoCancellation === true,
  };
}
