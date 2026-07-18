import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { setVoiceModeAllSessions, type SessionDto } from "@devthrottle/client-core/api/client";
import { getSessionsEnvelope } from "@devthrottle/client-core/fleet/fleetClient";
import { emptyRetentionCache, mergeRosterRetention, type RosterSessionMark } from "@devthrottle/client-core/fleet/rosterRetention";
import { classify, contextLine, deletionReason, dotHex, inBucket, inDesktopOrder, inWaitingOrder, isWorking, pendingDeletion, repoLeaf, snoozeCountdown, snoozeExpired } from "@devthrottle/client-core/sessions/ordering";
import { applyFilter, filterIsActive, filterSummary, machineName, pruneFilter } from "@devthrottle/client-core/sessions/filter";
import { useDictationStatusFor } from "@devthrottle/client-core/dictation/status";
import { useNow, waitingLabel } from "@devthrottle/client-core/sessions/waiting";
import { playClip, playingSid, rowVoiceInputs, stopPlayback, syncVoiceSessions, useVoiceClips } from "@devthrottle/client-core/voice/clips";
import { isVoiceReady, voiceRowState } from "@devthrottle/client-core/voice/voiceRowState";
import { NavDrawer } from "../components/NavDrawer";
import { StatusPill } from "../components/StatusPill";
import { SessionFilterPanel } from "../components/SessionFilterPanel";
import { useSessionFilter } from "../hooks/useSessionFilter";
import { enablePush, notificationPermission, pushSupported, reconcileBadge } from "@devthrottle/client-core/push/register";

// Home / roster. A "needs you" group first (when any session wants attention), then an "other
// sessions" group with everything that is NOT waiting on you - so a session appears in exactly one
// group and is never listed twice. Both use the live Gateway /sessions data and the shared triage
// ordering. Tapping a row opens the session-detail placeholder bound to that session id.
//
// The roster reads the /sessions ENVELOPE (per-Director reachability), not the flat list, and runs it
// through the keep-and-mark merge (mobile-resilience mission, Phase 2): a session whose owning machine
// is unreachable STAYS on the roster, grayed and marked, and leaves only when its Director answers
// without it. The retention cache is held in a ref so it survives across polls (and navigating into a
// session and back) without churning React state.
const POLL_INTERVAL_MS = 5000;

/** The roster's two lenses: the full roster, or only the sessions that can speak to you right now. */
type RosterTab = "all" | "voice";

export function Home() {
  const [sessions, setSessions] = useState<SessionDto[] | null>(null);
  // Per-session reachability marks from the merge - only unreachable (wobbly/offline) sessions have one.
  const [marks, setMarks] = useState<Map<string, RosterSessionMark>>(() => new Map());
  const retentionCache = useRef(emptyRetentionCache());
  const [error, setError] = useState<string | null>(null);
  // The roster filter (by machine and/or repo) and whether its full-screen panel is open. The filter
  // is persisted across navigations and restarts by the hook; the panel is transient UI state.
  const [filter, setFilter] = useSessionFilter();
  const [showFilter, setShowFilter] = useState(false);
  // The roster's two tabs. "All" is the roster as it has always been; "Voice" is the hands-free view -
  // only the sessions with narration ready to play THIS INSTANT, nothing else. When you are listening
  // rather than reading, a session that is working, executing, or has nothing to say is not a smaller
  // priority, it is noise, so the Voice tab does not rank it down - it leaves it out.
  //
  // Transient by design: it is a lens you pick up for a minute, not a mode you can get stranded in.
  // Persisting it (as the machine/repo filter is persisted) would mean coming back to the app hours
  // later, during an outage, to an empty roster and no memory of why.
  const [tab, setTab] = useState<RosterTab>("all");

  // Re-render the roster when a voice clip finishes downloading (a card flips from the yellow
  // working state to the play-triangle the moment its audio is phone-ready).
  useVoiceClips();

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const envelope = await getSessionsEnvelope(signal);
      // Keep-and-mark: retained sessions of unreachable machines stay on the roster, marked; a session
      // leaves only when its owning Director answered without it (mergeRosterRetention owns the rule).
      const merged = mergeRosterRetention(retentionCache.current, envelope);
      retentionCache.current = merged.cache;
      setSessions(merged.roster.sessions);
      setMarks(merged.roster.marks);
      setError(null);
      // The app-icon "needs you" dot and the voice-clip sync read the LIVE sessions only (never the
      // retained-and-marked ones): the badge must reflect what genuinely needs you right now, and only a
      // reachable session can have a phone-ready voice clip.
      void reconcileBadge(inBucket(envelope.sessions, "needsYou").length);
      // Pull each gateway-ready voice session's clip down to the phone so the triangle can appear
      // (phone-ready, the issue #850 rule). Fire-and-forget; it updates the clip store as bytes land.
      void syncVoiceSessions(envelope.sessions);
    } catch (err) {
      if (signal?.aborted) return;
      // Keep the last-known roster on screen (never clear good data on a bad connection). The global
      // ConnectionBanner - fed by the same failed contact through the shared health signal - is now the
      // single voice for "bad connection, showing last known", so this page no longer shows its own
      // offline strip. The error is kept only to stop the "Loading sessions..." line from lying after a
      // first-load failure.
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

  // Drop any filter selection whose machine or repo is no longer in the live roster, so a filter pinned
  // to a machine that went away cannot silently hide everything. pruneFilter returns the same reference
  // when nothing changed, so this only writes through (and re-renders) on a real change.
  useEffect(() => {
    if (!sessions) return;
    const pruned = pruneFilter(filter, sessions);
    if (pruned !== filter) setFilter(pruned);
  }, [sessions, filter, setFilter]);

  // The roster narrowed to the active machine/repo filter. Facet lists in the panel and the total
  // count in the app bar come from the FULL roster; only the displayed groups below are narrowed.
  const filtered = sessions ? applyFilter(sessions, filter) : sessions;
  // The Voice tab's roster: exactly the sessions whose row shows a play-triangle, by construction -
  // isVoiceReady IS "voiceRowState === ready", so the tab and the triangle can never disagree about
  // what "ready with voice" means. In waiting order, longest-waiting first, so it can be worked top to
  // bottom by ear.
  //
  // The reachability mark is fed in for the same reason the row feeds it: a retained session from an
  // unreachable machine keeps its last-known voiceAudioReady and its already-downloaded clip, so it
  // would otherwise sit in this tab claiming it can speak. This tab is the hands-free lens - the one
  // place a false "ready" is read out loud rather than looked at - so it must not list a dead machine.
  const voiceReady = filtered
    ? inWaitingOrder(filtered.filter((s) => isVoiceReady(rowVoiceInputs(s, isWorking(s), !marks.has(s.sessionId ?? "")))))
    : [];
  // The "Needs you" group is a waiting line: the session that has been waiting for you the longest
  // sits at the top, and a session that only just started needing you drops in at the bottom
  // (inWaitingOrder). This keeps the list from reshuffling under you as sessions change state, and
  // lets you work it top to bottom, dealing with the longest-neglected session first.
  const needsYou = filtered ? inWaitingOrder(filtered) : [];
  // The bottom group is "the rest": every session that is NOT waiting on you, still in your manual
  // desktop order. A needs-you session shows only once, at the top - never duplicated down here.
  const others = filtered ? inDesktopOrder(filtered.filter((s) => classify(s) !== "needsYou")) : [];
  const total = sessions ? sessions.length : 0;
  const shownTotal = filtered ? filtered.length : 0;
  const active = filterIsActive(filter);

  return (
    <div className="screen">
      <header className="app-bar">
        <NavDrawer />
        <h1>DevThrottle</h1>
        {/* Two spacers put the network pill in the MIDDLE of the bar, exactly as the session screens do
            (see SessionAppBar). It used to be a fixed top-right overlay that landed on top of the filter
            button in the corner; now it rides the row as an ordinary inline item. The "Mission Control"
            subtitle was dropped to give the pill this room - the header title already says what screen
            this is. GatedLayout stands the fixed pill down on the roster so it is not shown twice. */}
        <div className="app-bar-spacer" />
        <StatusPill inline />
        <div className="app-bar-spacer" />
        {/* The funnel opens the full-screen filter panel and doubles as the "filter active" indicator
            (a dot appears when a machine/repo filter is applied), so it is both the one-tap entry point
            and the status light - no separate menu item needed. */}
        <button
          type="button"
          className={`filter-btn${active ? " filter-btn-on" : ""}`}
          aria-label={active ? "Sessions filtered - edit filter" : "Filter sessions"}
          onClick={() => setShowFilter(true)}
        >
          <span className="filter-funnel" aria-hidden="true" />
        </button>
      </header>

      {/* When a filter is applied, a thin strip under the app bar names it and offers a one-tap Clear,
          so the filter can be seen and removed without reopening the panel. */}
      {active && (
        <div className="filter-strip" role="status">
          <span className="filter-funnel filter-strip-icon" aria-hidden="true" />
          <span className="filter-strip-text">{filterSummary(filter)}</span>
          <span className="filter-strip-count">{shownTotal} of {total}</span>
          <button type="button" className="filter-strip-clear" onClick={() => setFilter({ machines: [], repos: [] })}>
            Clear
          </button>
        </div>
      )}

      {showFilter && sessions !== null && (
        <SessionFilterPanel
          sessions={sessions}
          filter={filter}
          onApply={(next) => {
            setFilter(next);
            setShowFilter(false);
          }}
          onClose={() => setShowFilter(false)}
        />
      )}

      <EnableAlerts />

      {/* The roster's two lenses, in the same segmented control the session screen uses for its own
          views, so "Voice mode" means the same thing and looks the same wherever it appears. */}
      <div className="view-tabs" role="tablist" aria-label="Roster view">
        <button
          type="button"
          role="tab"
          aria-selected={tab === "all"}
          className={`view-tab${tab === "all" ? " active" : ""}`}
          onClick={() => setTab("all")}
        >
          All
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={tab === "voice"}
          className={`view-tab${tab === "voice" ? " active" : ""}`}
          onClick={() => setTab("voice")}
        >
          Voice mode
        </button>
      </div>

      {/* "+ New session" entry (issue #812): opens the add-session flow (machine -> repo -> create),
          a faithful translation of the Android NewSessionPanel. Hidden in the Voice tab: starting a new
          session is a typing job, and the Voice tab is for the sessions that can talk right now. */}
      {tab === "all" && (
        <Link className="new-session-entry" to="/new">
          <span className="new-session-plus" aria-hidden="true">+</span>
          New session
        </Link>
      )}

      {/* Car Mode's entry point is the nav drawer (top-left), NOT a banner here. It used to be both.
          The roster is a list of sessions that need you; a permanent full-width call-to-action for a
          different screen sat above that list drawing attention it had not earned - loudest element on
          a page it is not about, and shown just as insistently on an empty roster. It is one line in
          the drawer alongside every other destination, which is what it is. */}

      {sessions === null && error === null && <p className="status-line">Loading sessions...</p>}

      {sessions !== null && total === 0 && (
        <p className="status-line">No sessions running.</p>
      )}

      {/* The one-tap fleet-wide voice switch (issue #1765): "as I leave the house, put my whole fleet on
          voice; when I get home, take it all off". It reads the roster to decide its own action - offer
          "turn all on" while no session is a voice session, "turn all off" once any is - so the same
          button covers both halves of the walk-out / come-home round trip. Lives in the Voice tab, the
          voice-focused surface, and speaks to the Gateway which fans the change out to every session. */}
      {tab === "voice" && sessions !== null && total > 0 && (
        <VoiceAllControl sessions={sessions} />
      )}

      {/* The Voice tab, empty. Said plainly and without alarm: nothing is ready to listen to yet. The
          way out is one tap, so an empty voice roster is never a dead end. */}
      {tab === "voice" && sessions !== null && total > 0 && voiceReady.length === 0 && (
        <p className="status-line">
          Nothing to listen to right now.{" "}
          <button type="button" className="link-btn" onClick={() => setTab("all")}>
            Show all sessions
          </button>
        </p>
      )}

      {tab === "voice" && voiceReady.length > 0 && (
        <section className="group">
          <h2 className="group-title">Ready to play</h2>
          <ul className="roster">
            {voiceReady.map((s) => (
              <SessionRow key={`voice-${s.sessionId}`} session={s} mark={marks.get(s.sessionId ?? "")} />
            ))}
          </ul>
        </section>
      )}

      {/* Sessions exist but the active filter hides them all: say so plainly and offer a way back,
          instead of an empty screen that reads like "no sessions running". */}
      {tab === "all" && sessions !== null && total > 0 && shownTotal === 0 && (
        <p className="status-line">
          No sessions match this filter.{" "}
          <button type="button" className="link-btn" onClick={() => setFilter({ machines: [], repos: [] })}>
            Clear filter
          </button>
        </p>
      )}

      {tab === "all" && needsYou.length > 0 && (
        <section className="group">
          <h2 className="group-title group-title-attention">Needs you</h2>
          <ul className="roster">
            {needsYou.map((s) => (
              <SessionRow key={`needs-${s.sessionId}`} session={s} mark={marks.get(s.sessionId ?? "")} />
            ))}
          </ul>
        </section>
      )}

      {tab === "all" && others.length > 0 && (
        <section className="group">
          <h2 className="group-title">Other sessions</h2>
          <ul className="roster">
            {others.map((s) => (
              <SessionRow key={s.sessionId} session={s} mark={marks.get(s.sessionId ?? "")} />
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

// The fleet-wide voice switch shown at the top of the Voice tab (issue #1765). One control for the whole
// round trip: while no session is in voice mode it offers "Turn on voice for all N sessions"; the moment
// any session is a voice session it offers "Turn voice off for all sessions". Tapping calls the Gateway's
// one fan-out endpoint, which walks the roster itself, and shows a fail-loud summary of what changed and
// what was skipped (the mobile rule: never fail silently - name the offline sessions that were passed
// over). The next roster poll (5s) repaints each row's voice state, so the button flips to its opposite.
function VoiceAllControl({ sessions }: { sessions: SessionDto[] }) {
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const anyOn = sessions.some((s) => Boolean(s.voiceMode));
  // No session on voice yet -> the action is to turn the whole fleet ON; otherwise turn it all OFF.
  const enable = !anyOn;
  const count = sessions.length;

  const onClick = async () => {
    setBusy(true);
    setError(null);
    setNote(null);
    try {
      const result = await setVoiceModeAllSessions(enable);
      const changedLabel = `${result.changed} ${result.changed === 1 ? "session" : "sessions"} ${enable ? "on" : "off"}`;
      setNote(result.skipped > 0 ? `${changedLabel}, ${result.skipped} skipped (computer offline)` : changedLabel);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not change voice mode for all sessions");
    } finally {
      setBusy(false);
    }
  };

  const label = enable
    ? `Turn on voice for all ${count} ${count === 1 ? "session" : "sessions"}`
    : "Turn voice off for all sessions";
  const busyLabel = enable ? "Turning voice on..." : "Turning voice off...";

  return (
    <div className="voice-all">
      <button
        type="button"
        className={`voice-all-btn${enable ? "" : " voice-all-btn-off"}`}
        onClick={() => void onClick()}
        disabled={busy}
      >
        {busy ? busyLabel : label}
      </button>
      {note && <p className="voice-all-note" role="status">{note}</p>}
      {error && <p className="voice-all-error" role="alert">{error}</p>}
    </div>
  );
}

// The plain-English note under an unreachable card, naming the machine and (when known) how long ago it
// was last seen. Wobbly reads as a soft "reconnecting"; offline as a firm "unreachable" - both honest.
function unreachableNote(mark: RosterSessionMark): string {
  const machine = mark.machineName.length > 0 ? mark.machineName : "its machine";
  const base = mark.reachability === "wobbly" ? `Reconnecting to ${machine}` : `Unreachable - ${machine}`;
  return mark.lastSeenLabel.length > 0 ? `${base} - ${mark.lastSeenLabel}` : base;
}

function SessionRow({ session, mark }: { session: SessionDto; mark?: RosterSessionMark }) {
  const name = session.name && session.name.trim().length > 0 ? session.name : "(unnamed session)";
  const repo = repoLeaf(session);
  const machine = machineName(session);
  // An unreachable card (its owning machine is wobbly/offline, mobile-resilience Phase 2): grayed, its
  // stale attention state and live waiting timer suppressed, and a note naming the machine. It KEEPS its
  // last-known content and position - unreachable is shown, never deleted.
  const unreachable = mark !== undefined;
  const attention = classify(session) === "needsYou" && !unreachable;
  // Issue #844: the session's short three-digit number (SessionDto.Number, #820) read from the
  // regenerated typed client. Null on sessions/Directors without a number - then no prefix shows.
  const num = session.number;
  const hasNum = num !== null && num !== undefined && String(num).trim().length > 0;
  // The Gateway-owned hold time ("wakes in 3h 48m") and the winding-down flag, read the same way the
  // desktop rail and the Cockpit read them - never the raw onHold sensor (which can drift from the fold).
  const holdCountdown = snoozeCountdown(session);
  const windingDown = pendingDeletion(session);
  // Issue #948: a voice-mode session opens straight on its Voice tab (the surface it is meant to be
  // used from), not on the default Chat tab; every other session still opens on Chat.
  const sid = encodeURIComponent(session.sessionId ?? "");
  const to = session.voiceMode ? `/session/${sid}/voice` : `/session/${sid}`;
  return (
    <li className={`row${attention ? " row-attention" : ""}${unreachable ? " row-unreachable" : ""}`}>
      {/* Hand the known voice-mode state to the destination (issue #1015) so the Voice screen paints
          the right state on the first render instead of flashing OFF while its first poll resolves. */}
      <Link className="row-link" to={to} state={{ voiceMode: Boolean(session.voiceMode) }}>
        {/* The dot always paints the Gateway-stamped colour - even when the owning machine is unreachable,
            the dot keeps telling the truth about the session's last-known state. Unreachability is shown by
            the row treatment (the .row-unreachable dashed border + dimming opacity + the "Unreachable" note),
            never by overriding the dot with a locally chosen grey. */}
        <span className="dot" style={{ backgroundColor: dotHex(session) }} aria-hidden="true" />
        <span className="row-body">
          {/* The name uses the full card width and WRAPS (no truncation) - issue #838. A muted
              three-digit number prefix sits before the bold name, matching the desktop SessionRail
              (issue #844); when the session has no number, no prefix is rendered. */}
          <span className="row-name">
            {hasNum && <span className="row-num">{num}</span>}
            {name}
          </span>
          {/* The status / what-is-happening text sits on its own line below the name. On a needs-you
              card the live "waiting <dur>" is pinned to the right of this same line (issue #844). */}
          <span className="row-meta">
            <span className="row-context">{contextLine(session)}</span>
            {attention && session.needsYouSince && <WaitingTime since={String(session.needsYouSince)} />}
            {/* The hold time on a snoozed row ("wakes in 3h 48m"), from the Gateway-owned snooze clock,
                ticking each second - only mounted when there is a clock, so other rows keep no timer. */}
            {holdCountdown !== null && <HoldCountdown session={session} />}
          </span>
          {/* Snooze Length mission: a distinct "Snooze ended" badge when this session just returned from
              an expired snooze on its own (the dead-man's switch fired) - so the reader knows this is a
              "go see why it went quiet" item, not a fresh turn-end. */}
          {snoozeExpired(session) && <span className="row-snooze-ended">Snooze ended</span>}
          {/* A session flagged for deletion wears a neutral "winding down" badge (a BADGE, never a colour):
              the dot keeps telling the truth about the work while this rides beside it. */}
          {windingDown && (
            <span className="row-winding-down" title={deletionReason(session) ?? "Marked for deletion"}>
              winding down
            </span>
          )}
          {/* The facts you navigate and filter by - the machine the session runs on and its repo - are
              a bottom row of small chips, so a fleet spread across several machines is legible at a
              glance without crowding the status line. The machine chip is accent-tinted; the repo chip
              is neutral. Either is omitted when the Gateway did not stamp it. */}
          {(machine || repo) && (
            <span className="row-chips">
              {machine && <span className="row-chip row-chip-machine">{machine}</span>}
              {repo && <span className="row-chip row-chip-repo">{repo}</span>}
            </span>
          )}
          {/* Mobile-resilience Phase 2: when the owning machine is unreachable, a short plain note says so
              and names the machine - the card is kept and grayed, never dropped, so the reader knows this
              is stale-but-preserved, not gone. */}
          {mark && <span className="row-unreachable-note">{unreachableNote(mark)}</span>}
          {/* A dictation started on this session's screen keeps showing here once the user walks back
              to the roster (#1139): in-flight while it uploads/transcribes, a sticky red pill if it
              failed - so a dropped transcription is visible from the list, never silent. */}
          <DictationRowBadge sessionId={session.sessionId} />
        </span>
        {/* The voice control reads reachability for the same reason the dot, the attention state and
            the waiting timer above it do: on a retained card from an unreachable machine every one of
            those facts is last-known, and voice is no exception - it kept its clip and its
            last-known voiceAudioReady, so it was the one element still promising something live. */}
        <VoiceIndicator session={session} reachable={!unreachable} />
      </Link>
    </li>
  );
}

// Issue #850: the trailing voice control on a voice-mode card. What it is allowed to say is decided by
// the shared voiceRowState rule (client-core/voice/voiceRowState.ts), which this component only
// renders. That rule is shared with the Voice screen's own readiness test on purpose: the triangle is a
// promise that tapping through will speak, and the roster must not make that promise on weaker evidence
// than the screen it hands you to.
//
// It used to make exactly that mistake - it drew the triangle on `clip.phase === "ready"`, which asks
// "do I hold any bytes?" and never "which turn are they for?". A held clip from an older turn earned a
// green triangle; tapping it landed on "Voice service down". See voiceRowState.ts for the full account.
//
// Tapping the triangle plays the locally-stored clip with no download wait; preventDefault and
// stopPropagation keep the tap from also following the row's link. If this session's clip is playing
// when the agent resumes, it is stopped - a stale clip must not keep talking.
function VoiceIndicator({ session, reachable }: { session: SessionDto; reachable: boolean }) {
  const sid = session.sessionId ?? "";
  const working = isWorking(session);
  const state = voiceRowState(rowVoiceInputs(session, working, reachable));

  useEffect(() => {
    if (working && playingSid() === sid) stopPlayback();
  }, [working, sid]);

  if (state === "ready") {
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

  if (state === "preparing") {
    return <span className="row-spin" aria-label="Preparing voice" />;
  }

  // The Gateway cannot make voice for this session and said so. Say it on the row, quietly: the owner
  // asked to be told there is no voice rather than be handed a play button that leads to a dead screen.
  // No call to action - the Gateway is already retrying, and out-of-credits has its own banner.
  if (state === "down") {
    return <span className="row-voice-down">No voice</span>;
  }

  return null;
}

// The per-row dictation status pill (#1139 follow-up, honest states for #1182/#1184, the dropped states for
// #1590). Reads the same shared store the on-screen status strip reads, so a Speak Send shows on the roster
// too: a muted progress label while it is in flight, a calm amber "saved - still sending" while it is held on
// a bad connection, a "saved - tap to retry" while it is parked after a permanent failure, a red "Not sent -
// tap to open" when the session moved on and the words were dropped, and a red "Dictation failed" for the
// rare hard failure. A just-"done" send shows nothing here; the brief "Sent" acknowledgement belongs on the
// session screen, not as roster noise.
//
// Every non-busy phase is named EXPLICITLY, because the fallback below is a spinner: a phase that falls
// through would sit on the roster spinning forever, claiming a finished dictation is still working.
function DictationRowBadge({ sessionId }: { sessionId: string | undefined }) {
  const status = useDictationStatusFor(sessionId);
  if (!status || status.phase === "done") return null;
  if (status.phase === "failed") {
    return <span className="row-dictate row-dictate-failed">Dictation failed</span>;
  }
  if (status.phase === "dropped") {
    // The words were NOT delivered and are waiting on the session screen to be sent or dismissed.
    return <span className="row-dictate row-dictate-failed">Not sent - tap to open</span>;
  }
  if (status.phase === "unheard") {
    return <span className="row-dictate row-dictate-parked">Nothing heard</span>;
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

// The live "wakes in <dur>" hold time for a snoozed card, ticking each second from the Gateway-owned
// snooze clock (no roster refetch). Only mounted for snoozed cards that carry a clock, so the per-second
// re-render never touches other rows - the same pattern as WaitingTime.
function HoldCountdown({ session }: { session: SessionDto }) {
  const now = useNow(1000);
  const label = snoozeCountdown(session, now);
  if (label === null) return null;
  return <span className="row-hold-time">{label}</span>;
}
