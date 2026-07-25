import { authHeaders, creditsErrorFrom, gatewayFetch, GatewayError } from "../api/client";
import type { MicQualityReport } from "./micQuality";

// Gateway calls for the Test microphone and Test transcription checks (POST /voice-test/clip).
//
// Both checks send their clip here. The microphone check sends the audio and its measurements and asks
// for nothing back; the transcription check additionally sends the passage the user was reading and
// gets the transcript. One endpoint for both, because the point of storing them is to compare them,
// and two endpoints would be two schemas to reconcile at analysis time.
//
// Routed through gatewayFetch (never a raw fetch) so these calls feed the app-wide connection-health
// signal like every other Gateway contact.

export type VoiceTestKind = "microphone" | "transcription";

export interface VoiceTestUpload {
  kind: VoiceTestKind;
  /** The clip. The WAV the transcriber would receive, so what is stored is what was heard. */
  audio: Blob;
  /** BCP 47 primary subtag. Sent to the transcriber as a language hint and stored with the clip. */
  language?: string;
  /** The passage the user was asked to read. Transcription check only. */
  expected?: string;
  /** The microphone measurements, stored verbatim for later analysis. */
  quality?: MicQualityReport;
}

export interface VoiceTestResult {
  /** Identifier of the stored clip, or null when the Gateway could not store it. */
  clipId: string | null;
  /** What the transcriber returned. Empty for a microphone check. */
  transcript: string;
}

/**
 * Send a test clip to the Gateway. For a transcription check the response carries the transcript.
 *
 * A 402 becomes the shared credits error so the caller shows the standard add-credits message; any
 * other non-2xx throws a specific GatewayError. Nothing here fails silently: a check that cannot
 * reach the Gateway must say so, because a blank result would read as "your microphone is broken".
 */
export async function uploadVoiceTestClip(upload: VoiceTestUpload, signal?: AbortSignal): Promise<VoiceTestResult> {
  const form = new FormData();
  form.append("audio", upload.audio, `voice-test-${upload.kind}.wav`);
  form.append("kind", upload.kind);
  if (upload.language) form.append("language", upload.language);
  if (upload.expected) form.append("expected", upload.expected);
  if (upload.quality) form.append("quality", JSON.stringify(upload.quality));

  const res = await gatewayFetch("/voice-test/clip", {
    method: "POST",
    headers: { ...authHeaders() },
    body: form,
    signal,
  });
  if (!res.ok) {
    const body = (await res.json().catch(() => ({}))) as { error?: string };
    if (res.status === 402) throw creditsErrorFrom(body);
    throw new GatewayError(res.status, body.error ?? `The voice test failed: ${res.status}`);
  }
  const body = (await res.json().catch(() => ({}))) as { clipId?: string; transcript?: string };
  return { clipId: body.clipId ?? null, transcript: (body.transcript ?? "").trim() };
}
