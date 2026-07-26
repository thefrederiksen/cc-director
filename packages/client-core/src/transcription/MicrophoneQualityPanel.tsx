import { useCallback, useEffect, useState } from "react";
import {
  getMicrophoneQuality,
  type MicrophoneDeviceSummary,
  type MicrophoneQualitySummary,
} from "./microphoneQualityClient";
import "./microphoneQuality.css";

// "How your microphones are doing" - shared by the Cockpit's Transcription Health page and the
// Transcription settings tab on both surfaces.
//
// This is the BACKGROUND half of microphone quality: every dictation is measured automatically as it
// is sent, so this screen answers the question the on-demand Test microphone check cannot - which of
// your microphones is letting you down, across all the dictating you actually do. A headset that is
// bad only some of the time shows up here and nowhere else.
//
// Every verdict, every sentence of advice and the target figures are decided on the Gateway
// (MicrophoneQualityFold) and rendered here verbatim. This component contains no thresholds.
//
// It moved out of apps/cockpit when Settings was unified across the two surfaces: the phone shows the
// identical panel now, so it cannot live in one app's tree. Its styling came with it and is
// self-contained (mq-* only), rather than borrowing the health page's txh-* classes as it used to -
// the phone never loads that stylesheet.

function formatDb(value: number): string {
  return `${Math.round(value)} dB`;
}

function formatShare(share: number): string {
  return `${Math.round(share * 100)}%`;
}

export function MicrophoneQualityPanel() {
  const [summary, setSummary] = useState<MicrophoneQualitySummary | null>(null);
  const [loadError, setLoadError] = useState(false);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      setLoadError(false);
      setSummary(await getMicrophoneQuality(30, signal));
    } catch {
      if (signal?.aborted) return;
      setLoadError(true);
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  return (
    <div className="mq">
      <h2 className="mq-title">How your microphones are doing</h2>
      <p className="mq-lede">
        Every dictation you send is measured automatically, so a microphone that is quietly spoiling
        your transcripts shows up here without you having to go looking. Nothing is recorded - only how
        the audio measured.
      </p>

      {loadError ? (
        <div className="mq-load-error">
          Couldn&apos;t load microphone quality from the Gateway.
          <button type="button" className="mq-retry" onClick={() => void load()}>
            Retry
          </button>
        </div>
      ) : summary === null ? (
        <div className="mq-muted">Loading...</div>
      ) : (
        <>
          <div className={`mq-banner mq-${summary.status}`}>{summary.headline}</div>
          <p className="mq-lede">{summary.detail}</p>

          {summary.devices.length > 0 && (
            <div className="mq-devices">
              {summary.devices.map((d) => (
                <DeviceCard key={d.device} device={d} />
              ))}
            </div>
          )}

          {summary.totalSamples > 0 && (
            <p className="mq-muted">
              Based on {summary.totalSamples} measured{" "}
              {summary.totalSamples === 1 ? "dictation" : "dictations"} over the last 30 days.
            </p>
          )}
        </>
      )}
    </div>
  );
}

function DeviceCard({ device }: { device: MicrophoneDeviceSummary }) {
  return (
    <div className={`mq-device mq-device-${device.status}`}>
      <div className="mq-device-head">
        <span className="mq-device-name">{device.device}</span>
        <span className="mq-device-count">
          {device.samples} {device.samples === 1 ? "dictation" : "dictations"}
        </span>
      </div>
      <p className="mq-device-advice">{device.advice}</p>
      <dl className="mq-measures">
        <div>
          <dt>Your voice</dt>
          <dd>{formatDb(device.medianSpeechLevelDb)}</dd>
          {/* The target sits beside the reading rather than in a legend, so the comparison needs no
              second look and no knowledge of what a good number is. */}
          <span className="mq-target">good is about {formatDb(device.targetSpeechLevelDb)}</span>
        </div>
        <div>
          <dt>Voice above the room</dt>
          <dd>{formatDb(device.medianSignalToNoiseDb)}</dd>
          <span className="mq-target">good is {formatDb(device.targetSignalToNoiseDb)} or more</span>
        </div>
        <div>
          <dt>Telephone quality</dt>
          <dd>{formatShare(device.narrowbandShare)}</dd>
          <span className="mq-target">of dictations</span>
        </div>
        <div>
          <dt>Distorting</dt>
          <dd>{formatShare(device.clippingShare)}</dd>
          <span className="mq-target">of dictations</span>
        </div>
      </dl>
    </div>
  );
}
