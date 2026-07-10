import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { listSessions, type SessionDto } from "@devthrottle/client-core/api/client";
import { classify, contextLine, dotColor, effectiveColor, inBucket, inDesktopOrder, inWaitingOrder, isWorking, repoLeaf } from "@devthrottle/client-core/sessions/ordering";
import { useDictationStatusFor } from "@devthrottle/client-core/dictation/status";
import { useNow, waitingLabel } from "@devthrottle/client-core/sessions/waiting";
import { getClipState, playClip, playingSid, stopPlayback, syncVoiceSessions, useVoiceClips } from "../voice/clips";
import { NavDrawer } from "../components/NavDrawer";
import { enablePush, notificationPermission, pushSupported, reconcileBadge } from "../push/register";

// Home / roster. A "needs you" group first (when any session wants attention), then an "other
// sessions" group with everything that is NOT waiting on you - so a session appears in exactly one
// group and is never listed twice. Both use the live Gateway /sessions data and the shared triage
// ordering. Tapping a row opens the session-detail placeholder bound to that session id.
const POLL_INTERVAL_MS = 5000;

export function Home() {
  const [sessions, setSessions] = useState<SessionDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const loadedOnce = useRef(false);

  // Re-render the roster when a voice clip finishes downloading (a card flips from the yellow
  // working state to the play-triangle the moment its audio is phone-ready).
  useVoiceClips();

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const data = await listSessions(signal);
      setSessions(data);
      setError(null);
      loadedOnce.current = true;
      // Keep the app-icon "needs you" dot in sync while the app is open: set the badge to the live
      // count, or clear the badge and the service worker's dot notification when nothing is waiting.
      void reconcileBadge(inBucket(data, "needsYou").length);
      // Pull each gateway-ready voice session's clip down to the phone so the triangle can appear
      // (phone-ready, the issue #850 rule). Fire-and-forget; it updates the clip store as bytes land.
      void syncVoiceSessions(data);
    } catch (err) {
      if (signal?.aborted) return;
      // Keep the last-known roster on screen (offline shell); only show the error banner.
      setError(err instanceof Error ? err.message : "Failed to load sessions");
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    const timer = window.setInterval(() => {
      void load(controller.signal);
    }, POLL_INTERVAL_MS);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [load]);

  // The "Needs you" group is a waiting line: the session that has been waiting for you the longest
  // sits at the top, and a session that only just started needing you drops in at the bottom
  // (inWaitingOrder). This keeps the list from reshuffling under you as sessions change state, and
  // lets you work it top to bottom, dealing with the longest-neglected session first.
  const needsYou = sessions ? inWaitingOrder(sessions) : [];
  // The bottom group is "the rest": every session that is NOT waiting on you, still in your manual
  // desktop order. A needs-you session shows only once, at the top - never duplicated down here.
  const others = sessions ? inDesktopOrder(sessions.filter((s) => classify(s) !== "needsYou")) : [];
  const total = sessions ? sessions.length : 0;

  return (
    <div className="screen">
      <header className="app-bar">
        <NavDrawer />
        <h1>DevThrottle</h1>
        <span className="app-bar-sub">Mission Control</span>
      </header>

      {error !== null && (
        <div className="banner banner-error" role="alert">
          {loadedOnce.current ? "Offline - showing last-known roster" : error}
        </div>
      )}

      <EnableAlerts />

      {/* "+ New session" entry (issue #812): opens the add-session flow (machine -> repo -> create),
          a faithful translation of the Android NewSessionPanel. */}
      <Link className="new-session-entry" to="/new">
        <span className="new-session-plus" aria-hidden="true">+</span>
        New session
      </Link>

      {sessions === null && error === null && <p className="status-line">Loading sessions...</p>}

      {sessions !== null && total === 0 && (
        <p className="status-line">No sessions running.</p>
      )}

      {needsYou.length > 0 && (
        <section className="group">
          <h2 className="group-title group-title-attention">Needs you</h2>
          <ul className="roster">
            {needsYou.map((s) => (
              <SessionRow key={`needs-${s.sessionId}`} session={s} />
            ))}
          </ul>
        </section>
      )}

      {others.length > 0 && (
        <section className="group">
          <h2 className="group-title">Other sessions</h2>
          <ul className="roster">
            {others.map((s) => (
              <SessionRow key={s.sessionId} session={s} />
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}

// A one-time prompt to turn on the app-icon "needs you" dot. Shown only when the browser can do Web
// Push and the user has not decided yet; tapping requests permission (the gesture iOS/Android require)
// and subscribes. Hidden once granted, and replaced by a short hint if the user blocked notifications.
function EnableAlerts() {
  const [state, setState] = useState<NotificationPermission | "unsupported">(() => notificationPermission());
  const [busy, setBusy] = useState(false);

  if (!pushSupported() || state === "granted") return null;

  if (state === "denied") {
    return (
      <div className="banner banner-info" role="status">
        Notifications are blocked. Enable them for DevThrottle in your browser settings to see the
        app-icon dot when a session needs you.
      </div>
    );
  }

  const onEnable = async () => {
    setBusy(true);
    try {
      await enablePush();
    } catch (err) {
      console.warn("[push] enable failed:", err);
    } finally {
      setState(notificationPermission());
      setBusy(false);
    }
  };

  return (
    <div className="banner banner-info banner-action" role="status">
      <span>Get an app-icon dot when a session needs you.</span>
      <button type="button" className="banner-btn" onClick={() => void onEnable()} disabled={busy}>
        {busy ? "Enabling..." : "Enable notifications"}
      </button>
    </div>
  );
}

function SessionRow({ session }: { session: SessionDto }) {
  const color = effectiveColor(session);
  const name = session.name && session.name.trim().length > 0 ? session.name : "(unnamed session)";
  const repo = repoLeaf(session);
  const attention = classify(session) === "needsYou";
  // Issue #844: the session's short three-digit number (SessionDto.Number, #820) read from the
  // regenerated typed client. Null on sessions/Directors without a number - then no prefix shows.
  const num = session.number;
  const hasNum = num !== null && num !== undefined && String(num).trim().length > 0;
  // Issue #948: a voice-mode session opens straight on its Voice tab (the surface it is meant to be
  // used from), not on the default Chat tab; every other session still opens on Chat.
  const sid = encodeURIComponent(session.sessionId ?? "");
  const to = session.voiceMode ? `/session/${sid}/voice` : `/session/${sid}`;
  return (
    <li className={`row${attention ? " row-attention" : ""}`}>
      {/* Hand the known voice-mode state to the destination (issue #1015) so the Voice screen paints
          the right state on the first render instead of flashing OFF while its first poll resolves. */}
      <Link className="row-link" to={to} state={{ voiceMode: Boolean(session.voiceMode) }}>
        <span className="dot" style={{ backgroundColor: dotColor(color) }} aria-hidden="true" />
        <span className="row-body">
          {/* The name uses the full card width and WRAPS (no truncation) - issue #838. A muted
              three-digit number prefix sits before the bold name, matching the desktop SessionRail
              (issue #844); when the session has no number, no prefix is rendered. */}
          <span className="row-name">
            {hasNum && <span className="row-num">{num}</span>}
            {name}
          </span>
          {/* The status / what-is-happening text and the repo share one line BELOW the name,
              separated by a thin divider, with the repo kept visually secondary. On a needs-you
              card the live "waiting <dur>" is pinned to the right of this same line (issue #844). */}
          <span className="row-meta">
            <span className="row-context">{contextLine(session)}</span>
            {repo && <span className="row-divider" aria-hidden="true" />}
            {repo && <span className="row-repo">{repo}</span>}
            {attention && session.needsYouSince && <WaitingTime since={String(session.needsYouSince)} />}
          </span>
          {/* A dictation started on this session's screen keeps showing here once the user walks back
              to the roster (#1139): in-flight while it uploads/transcribes, a sticky red pill if it
              failed - so a dropped transcription is visible from the list, never silent. */}
          <DictationRowBadge sessionId={session.sessionId} />
        </span>
        <VoiceIndicator session={session} />
      </Link>
    </li>
  );
}

// Issue #850: the trailing voice control on a voice-mode card. A play-triangle appears ONLY once
// the clip's audio is on the phone (clip phase "ready"); while the Wingman is generating on the
// Gateway or the phone is still downloading, a yellow spinner shows instead. Non-voice sessions
// render nothing here. Tapping the triangle plays the locally-stored clip with no download wait;
// preventDefault/stopPropagation keep the tap from also following the row's link.
//
// The finished-turn narration is retired the instant the session starts working again: while
// isWorking(session) is true the whole indicator renders nothing (no triangle, no spinner), because
// that verbal cue is now stale. If this session's clip is playing at that moment it is stopped, so a
// stale clip cannot keep talking after the agent has resumed.
function VoiceIndicator({ session }: { session: SessionDto }) {
  const sid = session.sessionId ?? "";
  const working = isWorking(session);

  useEffect(() => {
    if (working && playingSid() === sid) stopPlayback();
  }, [working, sid]);

  if (!session.voiceMode) return null;
  if (working) return null;

  const clip = getClipState(sid);

  if (clip.phase === "ready") {
    const isPlaying = playingSid() === sid;
    return (
      <button
        type="button"
        className="row-tri-btn"
        aria-label={isPlaying ? "Stop voice message" : "Play voice message"}
        onClick={(e) => {
          e.preventDefault();
          e.stopPropagation();
          if (isPlaying) stopPlayback();
          else playClip(sid);
        }}
      >
        <span className={isPlaying ? "row-stop" : "row-tri"} aria-hidden="true" />
      </button>
    );
  }

  // Voice on but no phone-ready clip yet: generating on the Gateway, or downloading to the phone.
  if (session.voiceGenerating || session.voiceAudioReady || clip.phase === "downloading") {
    return <span className="row-spin" aria-label="Preparing voice" />;
  }

  return null;
}

// The per-row dictation status pill (#1139 follow-up, honest states for #1182/#1184). Reads the same shared
// store the on-screen status strip reads, so a Speak Send shows on the roster too: a muted progress label
// while it is in flight, a calm amber "saved - still sending" while it is held on a bad connection, a
// "saved - tap to retry" while it is parked after a permanent failure, and a red "Dictation failed" for the
// rare hard failure. A just-"done" send shows nothing here; the brief "Sent" acknowledgement belongs on the
// session screen, not as roster noise.
function DictationRowBadge({ sessionId }: { sessionId: string | undefined }) {
  const status = useDictationStatusFor(sessionId);
  if (!status || status.phase === "done") return null;
  if (status.phase === "failed") {
    return <span className="row-dictate row-dictate-failed">Dictation failed</span>;
  }
  if (status.phase === "held") {
    return <span className="row-dictate row-dictate-held">Saved - still sending</span>;
  }
  if (status.phase === "parked") {
    return <span className="row-dictate row-dictate-parked">Saved - tap to retry</span>;
  }
  return (
    <span className="row-dictate row-dictate-busy">
      <span className="row-spin" aria-hidden="true" /> {rowBusyLabel(status.phase, status.uploaded, status.total)}
    </span>
  );
}

// The compact roster label for an in-flight dictation, mirroring the on-screen strip's phases so the
// roster is honest about where the send is (saving / uploading N of M / transcribing).
function rowBusyLabel(phase: string, uploaded?: number, total?: number): string {
  if (phase === "saving") return "Saving...";
  if (phase === "uploading") {
    if (total && total > 1) return `Uploading... ${uploaded ?? 0} of ${total}`;
    return "Uploading...";
  }
  return "Transcribing...";
}

// Issue #844: the live elapsed-waiting label for a needs-you card, right-aligned on the status
// line. It ticks once a second by recomputing from the held needsYouSince (no roster refetch), and
// renders nothing while the value is empty/unparseable. Only mounted for needs-you cards, so the
// per-second re-render never touches working/other rows.
function WaitingTime({ since }: { since: string }) {
  const now = useNow(1000);
  const label = waitingLabel(since, now);
  if (label.length === 0) return null;
  return <span className="row-waiting">{label}</span>;
}
