// The fleet-level Wingman-pipeline observability surface of the Gateway (issue #976, epic #967): the
// typed, same-origin client the React Cockpit's Wingman Pipeline page reads. It is the shared-library
// port of the Blazor Cockpit's GatewayClient.GetWingmanQueueAsync, so the desktop React shell keeps
// exactly one copy of the /wingman/queue contract.
//
// This is OBSERVABILITY ONLY - a read of GET /wingman/queue. No verb here mutates queue state (the
// page is read-only by design). The request is root-relative to the Gateway front door (the
// Gateway-only-ingress rule) and carries the same Bearer via authHeaders(); a non-2xx throws
// GatewayError so the page surfaces the real reason instead of a silently blank pipeline.
//
// The response is not in the generated OpenAPI schema (the endpoint returns via Results.Json without
// a [Produces] annotation), so these are narrow local mirrors of the C# WingmanQueueDto family
// (CcDirector.Gateway.Contracts), the same pattern api/client uses for the other Gateway-native shapes.
import { authHeaders, GatewayError } from "../api/client";

/** The session the brain is reading right now. */
export interface WingmanInFlight {
  sessionId: string;
  /** "brief" (background turn-brief stamping) or "explain" (user-initiated deep dive). */
  kind: string;
  /** Seconds since this session entered the in-flight slot (display only). */
  elapsedSeconds: number;
}

/** One session waiting behind the in-flight one. */
export interface WingmanQueueEntry {
  sessionId: string;
  /** "brief" (a turn end is queued) or "explain" (an explain deep dive is queued). */
  kind: string;
}

/** One recently completed brief, for the "recent" list. */
export interface WingmanRecentBrief {
  sessionId: string;
  turnNumber: number;
  generatedAtUtc: string;
  /** True when the brief came from a degrade tier, not the wingman - the poisoned-brain tell. */
  degraded: boolean;
  /** Generator identity that wrote the brief, e.g. "gateway-brain/opus" or "stub". */
  model: string;
}

/** The warm brain's health block. */
export interface WingmanBrainHealth {
  /** PID of the hosted brain process; 0 before first use / after death. */
  pid: number;
  model: string;
  /** True when the brain process is running and prompt-accepting. */
  alive: boolean;
  /** Process lifecycle status: NotStarted / Starting / Running / Exiting / Exited / Failed / Disabled. */
  status: string;
  /** Consecutive validation rejections (the poisoned-brain signal). */
  consecutiveRejections: number;
  /** The rejection streak length that triggers a recovery restart. */
  rejectionThreshold: number;
  /** True while a brain recovery restart is in flight. */
  recoveryInFlight: boolean;
}

/** A read-only snapshot of the one-brain wingman pipeline at one instant. */
export interface WingmanQueueSnapshot {
  /** The session currently being read, or null when the pipeline is idle. */
  inFlight: WingmanInFlight | null;
  /** The ordered list of sessions waiting behind the in-flight one. Empty when idle. */
  queue: WingmanQueueEntry[];
  /** The most recent completed briefs (bounded last-N), newest first. */
  recent: WingmanRecentBrief[];
  /** The warm brain's health. */
  brain: WingmanBrainHealth;
}

// GET /wingman/queue - the read-only pipeline snapshot. Since issue #549 retired the always-on
// stamping machine, current Gateways answer an honest idle snapshot with brain.status "Disabled";
// the page renders that honestly. Missing fields default so an older/leaner body still parses.
export async function getWingmanQueue(signal?: AbortSignal): Promise<WingmanQueueSnapshot> {
  const res = await fetch("/wingman/queue", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) {
    throw new GatewayError(res.status, `GET /wingman/queue failed: ${res.status}`);
  }
  const body = (await res.json()) as Partial<WingmanQueueSnapshot> & {
    inFlight?: Partial<WingmanInFlight> | null;
    brain?: Partial<WingmanBrainHealth>;
  };
  const brain: Partial<WingmanBrainHealth> = body.brain ?? {};
  return {
    inFlight: body.inFlight
      ? {
          sessionId: body.inFlight.sessionId ?? "",
          kind: body.inFlight.kind ?? "brief",
          elapsedSeconds: Number(body.inFlight.elapsedSeconds ?? 0),
        }
      : null,
    queue: (body.queue ?? []).map((q) => ({ sessionId: q.sessionId ?? "", kind: q.kind ?? "brief" })),
    recent: (body.recent ?? []).map((r) => ({
      sessionId: r.sessionId ?? "",
      turnNumber: Number(r.turnNumber ?? 0),
      generatedAtUtc: r.generatedAtUtc ?? "",
      degraded: Boolean(r.degraded),
      model: r.model ?? "",
    })),
    brain: {
      pid: Number(brain.pid ?? 0),
      model: brain.model ?? "",
      alive: Boolean(brain.alive),
      status: brain.status ?? "",
      consecutiveRejections: Number(brain.consecutiveRejections ?? 0),
      rejectionThreshold: Number(brain.rejectionThreshold ?? 0),
      recoveryInFlight: Boolean(brain.recoveryInFlight),
    },
  };
}
