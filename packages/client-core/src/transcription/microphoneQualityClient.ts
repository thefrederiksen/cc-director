import { authHeaders, gatewayFetch, GatewayError } from "../api/client";

// Reads the folded microphone-quality verdict from the Gateway (GET /voice-quality/summary).
//
// Every judgement below - the headline, the per-device status, the advice sentence, and what "good"
// looks like - arrives ALREADY DECIDED. This client applies no thresholds and derives no verdicts;
// it is a typed hole in the wall. That is the standing rule for this product: one place folds the
// meaning, the screens render it, so a new verdict is one Gateway edit rather than a new branch in
// every client.

export interface MicrophoneDeviceSummary {
  device: string;
  /** The stable identifier the Gateway grouped this device's measurements by. Empty when unknown. */
  deviceId: string;
  /** "mobile" | "mac" | "windows" | "unknown" - folded on the Gateway. */
  platform: string;
  /** The finished display string for the platform. Empty when unknown, so the screen renders
   *  nothing rather than a guess. */
  platformLabel: string;
  samples: number;
  /** "good" | "learning" | "bad". Drives colour only - the words come from `advice`. */
  status: string;
  advice: string;
  narrowbandShare: number;
  clippingShare: number;
  medianSpeechLevelDb: number;
  medianSignalToNoiseDb: number;
  /** What a healthy microphone reads, folded alongside so the screen can COMPARE, not just display. */
  targetSpeechLevelDb: number;
  targetSignalToNoiseDb: number;
  lastSeenUtc: string;
}

export interface MicrophoneQualitySummary {
  totalSamples: number;
  /** "empty" | "learning" | "good" | "bad". */
  status: string;
  headline: string;
  detail: string;
  devices: MicrophoneDeviceSummary[];
}

/** One day of one device's dictation, folded to medians and shares on the Gateway. */
export interface MicrophoneTrendPoint {
  /** The calendar day, UTC, as yyyy-MM-dd. */
  date: string;
  samples: number;
  medianSpeechLevelDb: number;
  medianSignalToNoiseDb: number;
  narrowbandShare: number;
  clippingShare: number;
}

/** One dictation's measurement - how the audio sounded, never what was said. */
export interface MicrophoneMeasurement {
  timestampUtc: string;
  source: string;
  durationSeconds: number;
  sampleRate: number;
  speechLevelDb: number;
  noiseFloorDb: number;
  signalToNoiseDb: number;
  clippedFraction: number;
  narrowband: boolean;
  rating: string;
  issues: string;
}

/** One microphone in full: the same folded verdict as the summary, plus its history. */
export interface MicrophoneDeviceDetail {
  summary: MicrophoneDeviceSummary;
  /** The raw evidence behind the platform bucket, for diagnosing a wrong bucket. */
  platformRaw: string;
  /** One point per calendar day with data, oldest first - the quality-over-time series. */
  trend: MicrophoneTrendPoint[];
  /** How many measurements the window really holds; when it exceeds measurements.length the list
   *  was capped at the newest ones and the screen must say so. */
  measurementsTotal: number;
  /** Individual measurements, newest first, capped by the Gateway. */
  measurements: MicrophoneMeasurement[];
}

export interface MicrophoneQualityDetail {
  totalSamples: number;
  /** "empty" | "learning" | "good" | "bad" - the same verdict the summary carries. */
  status: string;
  headline: string;
  detail: string;
  devices: MicrophoneDeviceDetail[];
}

export async function getMicrophoneQuality(days?: number, signal?: AbortSignal): Promise<MicrophoneQualitySummary> {
  const query = days === undefined ? "" : `?days=${days}`;
  const res = await gatewayFetch(`/voice-quality/summary${query}`, { headers: { ...authHeaders() }, signal });
  if (!res.ok) {
    const body = (await res.json().catch(() => ({}))) as { error?: string };
    throw new GatewayError(res.status, body.error ?? `Could not read microphone quality: ${res.status}`);
  }
  return (await res.json()) as MicrophoneQualitySummary;
}

/** The detailed per-device picture (GET /voice-quality/detail): the summary's verdicts plus each
 *  device's daily quality-over-time series and the individual measurements behind it. */
export async function getMicrophoneQualityDetail(days?: number, signal?: AbortSignal): Promise<MicrophoneQualityDetail> {
  const query = days === undefined ? "" : `?days=${days}`;
  const res = await gatewayFetch(`/voice-quality/detail${query}`, { headers: { ...authHeaders() }, signal });
  if (!res.ok) {
    const body = (await res.json().catch(() => ({}))) as { error?: string };
    throw new GatewayError(res.status, body.error ?? `Could not read microphone detail: ${res.status}`);
  }
  return (await res.json()) as MicrophoneQualityDetail;
}

/** Forget every measurement for this account. Returns how many daily files were removed. */
export async function clearMicrophoneQuality(signal?: AbortSignal): Promise<number> {
  const res = await gatewayFetch("/voice-quality/history", {
    method: "DELETE",
    headers: { ...authHeaders() },
    signal,
  });
  if (!res.ok) throw new GatewayError(res.status, `Could not clear microphone quality: ${res.status}`);
  const body = (await res.json().catch(() => ({}))) as { removed?: number };
  return body.removed ?? 0;
}
