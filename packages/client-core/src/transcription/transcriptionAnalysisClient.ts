// Client for the Gateway's local transcription analysis API (GET /transcription/*). It reads the
// on-machine transcription history the Gateway records for every turn - latency, outcomes, and the
// dictionary corrections that were applied - so the Cockpit (and any agent) can
// see how fast and how good transcription is. Same-origin through the Gateway front door with the
// per-device key, exactly like the rest of client-core; never a Director address.

import { authHeaders } from "../api/client";

export interface Percentiles {
  count: number;
  min: number;
  max: number;
  avg: number;
  p50: number;
  p90: number;
  p95: number;
  p99: number;
}

export interface TranscriptionStats {
  totalTurns: number;
  successfulTurns: number;
  byOutcome: Record<string, number>;
  firstTurnUtc: string | null;
  lastTurnUtc: string | null;
  transcriptionMs: Percentiles;
  cleanupMs: Percentiles;
  correctedTurns: number;
  cleanupAppliedTurns: number;
  totalWords: number;
  totalCharacters: number;
}

export interface TermFrequency {
  find: string;
  replace: string;
  count: number;
}

/** The outcome code the Gateway records for a dictation that transcribed successfully. Every other
 * code in byOutcome is a failure. This is the one place that defines what "success" means, so the
 * failure count, the success banner, and the outcome breakdown all agree. */
export const SUCCESS_OUTCOME = "ok";

/**
 * How many dictations did not succeed in the window, counted as the sum of every non-success entry
 * of the byOutcome map. This is the single authority for the failure count: the outcome breakdown
 * on the page also reads byOutcome, so the failure number, the success banner, and that breakdown
 * can never disagree. Deriving it here - rather than as totalTurns minus successfulTurns - removes
 * the second, separately-computed number that could drift from the map the page renders.
 */
export function countFailedTurns(stats: TranscriptionStats): number {
  let failed = 0;
  for (const [code, count] of Object.entries(stats.byOutcome)) {
    if (code !== SUCCESS_OUTCOME) failed += count;
  }
  return failed;
}

async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(path, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) {
    throw new Error(`GET ${path} failed: ${res.status}`);
  }
  return (await res.json()) as T;
}

function windowQuery(days?: number): string {
  return days && days > 0 ? `?days=${days}` : "";
}

/** Aggregate transcription stats over the last {days} days (or all time when omitted). */
export function getTranscriptionStats(days?: number, signal?: AbortSignal): Promise<TranscriptionStats> {
  return getJson<TranscriptionStats>(`/transcription/stats${windowQuery(days)}`, signal);
}

/** Delete all locally retained Transcription Health history. */
export async function clearTranscriptionHistory(signal?: AbortSignal): Promise<number> {
  const res = await fetch("/transcription/history", {
    method: "DELETE",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw new Error(`DELETE /transcription/history failed: ${res.status}`);
  const body = (await res.json()) as { removedFiles?: number };
  return Number(body.removedFiles ?? 0);
}

/** The most frequent dictionary corrections applied, over the window. */
export async function getTranscriptionTerms(
  top = 10,
  days?: number,
  signal?: AbortSignal,
): Promise<TermFrequency[]> {
  const q = windowQuery(days);
  const sep = q ? "&" : "?";
  const data = await getJson<{ terms: TermFrequency[] }>(
    `/transcription/terms${q}${sep}top=${top}`,
    signal,
  );
  return data.terms ?? [];
}
