// The Wingman feedback corpus surface of the Gateway (issue #978, epic #967): the typed, same-origin
// client the React Cockpit's Feedback page reads. It is the shared-library port of the Blazor
// Cockpit's GatewayClient.GetBriefFeedbackAsync.
//
// This is the READ side of the brief vote/reason corpus (issue #207): every record is a saved brief
// vote with the full brief headline/rail line and whether a replayable TurnPackage was captured. The
// page lists recent records; it does not write. Every request is root-relative to the Gateway
// (GET /turnbriefs/feedback) and carries the same Bearer via authHeaders().
//
// NOTE (scope clarity): this is NOT the desktop "Help > Send Feedback" flow that files a GitHub issue
// with a screenshot on the feedback-assets branch (that is CcDirector.Core.Feedback.FeedbackService, a
// desktop-app feature the Cockpit never served). The Cockpit's Feedback page is this Wingman
// feedback-corpus reader, and this port matches it one-to-one.
import { authHeaders, GatewayError } from "../api/client";

/** One record in the Wingman feedback corpus, as GET /turnbriefs/feedback projects it. */
export interface BriefFeedbackItem {
  feedbackId: string;
  sessionId: string;
  turnNumber: number;
  /** "up" (useful) or "down" (wrong). */
  vote: string;
  reason: string;
  brainModel: string;
  briefHeadline: string;
  briefRailLine: string;
  hasTurnPackage: boolean;
  /** ISO 8601 UTC when the vote was reported. */
  reportedAtUtc: string;
}

/** The GET /turnbriefs/feedback envelope. */
export interface BriefFeedbackListResponse {
  items: BriefFeedbackItem[];
}

async function gatewayErrorFrom(res: Response, label: string): Promise<GatewayError> {
  let detail = `${res.status}`;
  try {
    const text = await res.text();
    if (text.length > 0) {
      try {
        const body = JSON.parse(text) as { error?: string; detail?: string };
        detail = body.error ?? body.detail ?? text;
      } catch {
        detail = text;
      }
    }
  } catch {
    /* body unreadable - keep the status code */
  }
  return new GatewayError(res.status, `${label} failed: ${detail}`);
}

// GET /turnbriefs/feedback[?count=N] - the recent Wingman feedback corpus records, newest first (the
// Gateway store already orders them). Throws on transport failure so the Feedback page shows an error
// state rather than a fabricated empty corpus.
export async function getBriefFeedback(count = 100, signal?: AbortSignal): Promise<BriefFeedbackItem[]> {
  const path = count > 0 ? `/turnbriefs/feedback?count=${count}` : "/turnbriefs/feedback";
  const res = await fetch(path, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "GET /turnbriefs/feedback");
  const body = (await res.json()) as Partial<BriefFeedbackListResponse> | null;
  return body?.items ?? [];
}
