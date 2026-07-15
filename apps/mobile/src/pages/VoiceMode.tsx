import { useEffect, useState } from "react";
import { useLocation, useNavigate, useParams } from "react-router-dom";
import { DictationDialog } from "@devthrottle/client-core/dictation/DictationDialog";
import { formatClock, useVoiceMode } from "@devthrottle/client-core/voice/useVoiceMode";
import { DictationStatusStrip } from "../components/DictationStatusStrip";
import { SessionAppBar } from "../components/SessionAppBar";
import { useSessionManage } from "../components/useSessionManage";
import { ViewTabs } from "../components/ViewTabs";

// Session Voice mode (issue #850): the hands-free Wingman narration screen, the third session view
// alongside Terminal (#817) and Chat (#811). This component is a THIN view - all of its state, the
// poll, the state-machine derivation, the playback handlers, and the clip management live in the
// shared useVoiceMode hook (packages/client-core/src/voice/useVoiceMode.ts), so the phone app and the
// Cockpit render the identical screen from one copy of the logic (issue #1213, plan phase 4). The
// only thing this file owns is the router wiring (session id + the roster's voice-mode seed) and the
// JSX; nothing here changes behavior from the pre-hoist page.

export function VoiceMode() {
  const { sessionId } = useParams<{ sessionId: string }>();

  // The roster hands the known voice-mode state on navigation (issue #1015), so the screen renders
  // the right state on the FIRST paint instead of flashing OFF while its first poll resolves. Read it
  // from the router here and pass it into the hook - the shared hook stays router-free.
  //
  // switchOn arrives from "Switch to voice mode" in the Chat/Terminal overflow menu (SessionAppBar):
  // that menu item navigates here and asks the hook to run its own onSwitchOn, so the one place that
  // knows how to enter voice mode stays the one place that does it.
  const location = useLocation();
  const navigate = useNavigate();
  const navState = location.state as { voiceMode?: boolean; switchOn?: boolean } | null;
  const seededVoiceOn = navState?.voiceMode;

  // switchOn is a ONE-SHOT COMMAND, so it is read once and then scrubbed off the history entry.
  //
  // It cannot be read straight from location.state on every render: router state is persisted in the
  // history entry and survives a reload, while the hook's "already did it" guard is only a ref, which a
  // remount resets. So the command would fire AGAIN on any remount of this history entry - turn voice
  // on from the Chat menu, turn it off, let the service worker update reload the app (it does, on every
  // deploy), and voice would switch itself back on against your explicit wish. Caught in review of
  // #1631.
  //
  // useState's initializer captures the command exactly once, at mount; the effect then rewrites the
  // entry with switchOn cleared, so any later remount reads a spent command and does nothing.
  const [autoSwitchOn] = useState(() => navState?.switchOn === true);
  useEffect(() => {
    if (navState?.switchOn !== true) return;
    navigate(location.pathname + location.search, {
      replace: true,
      state: { ...navState, switchOn: false },
    });
    // Mount-only: this consumes the arriving command. Re-running it on navState changes would fight
    // the very rewrite it performs.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const {
    voiceOn,
    speaking,
    working,
    audioUnavailable,
    unavailableReason,
    unavailableIsServiceDown,
    agentWorking,
    pollDone,
    narrative,
    title,
    error,
    resumed,
    playing,
    pos,
    dur,
    enabling,
    regenerating,
    enableNote,
    responding,
    setResponding,
    setPlaying,
    clipUrl,
    clipPhase,
    onSwitchOn,
    onSwitchOff,
    onGenerateNow,
    setAudioEl,
    onLoadedMeta,
    onTimeUpdate,
    onEndedAudio,
    onSeek,
    onRestart,
    onTogglePlay,
    onRespondSend,
    onRespondSendAudio,
  } = useVoiceMode(sessionId, { seededVoiceOn, autoSwitchOn });

  // Snooze and Remove share this one live held-state, so the bottom bar and the overflow menu can
  // never disagree about it.
  const manage = useSessionManage(sessionId);

  return (
    <div className="terminal-screen">
      {/* Snooze is NOT in this menu - the bottom bar owns it on this screen (owner: "I press the
          snooze button a lot"), and a verb in two places is a verb that disagrees with itself. */}
      <SessionAppBar
        title={title}
        manage={manage}
        extraMenuItems={
          voiceOn ? (
            <>
              {!audioUnavailable && (
                <button
                  type="button"
                  className="menu-item"
                  role="menuitem"
                  onClick={() => void onGenerateNow()}
                  disabled={regenerating}
                >
                  {regenerating ? "Regenerating..." : "Regenerate response"}
                </button>
              )}
              <button
                type="button"
                className="menu-item"
                role="menuitem"
                onClick={() => void onSwitchOff()}
              >
                Turn voice off
              </button>
            </>
          ) : undefined
        }
      />

      <ViewTabs sessionId={sessionId} active="voice" />

      {/* Live dictation status so a spoken reply Send is never silent (#1139). */}
      <DictationStatusStrip sessionId={sessionId} />

      {/* The clip element is always mounted (hidden) so auto-play works in any state; the visible
          play controls live in the speaking state below. */}
      <audio
        ref={setAudioEl}
        src={clipUrl ?? undefined}
        preload="auto"
        onLoadedMetadata={onLoadedMeta}
        onTimeUpdate={onTimeUpdate}
        onPlay={() => setPlaying(true)}
        onPause={() => setPlaying(false)}
        onEnded={onEndedAudio}
        style={{ display: "none" }}
      />

      {/* In the speaking state the audio controls are a FIXED header and only the narrative text
          scrolls, in its own window below them (voice-body-speaking). In the other states the body
          scrolls normally. */}
      <div className={`voice-body${speaking ? " voice-body-speaking" : ""}`}>
        {error !== null && (
          <div className="banner banner-error" role="alert">{error}</div>
        )}

        {/* A. OFF - one clear "Switch to voice mode" button. Only ever shown once a poll has confirmed
            the session is NOT in voice mode, so the screen never flashes OFF then flips (issue #1015). */}
        {!voiceOn && pollDone && (
          <div className="voice-off">
            <p className="voice-off-title">Voice mode is off for this session.</p>
            <p className="voice-hint">Turn it on and the Wingman will start narrating every turn.</p>
            <button type="button" className="voice-switch" onClick={onSwitchOn} disabled={enabling}>
              {enabling ? "Switching..." : "Switch to voice mode"}
            </button>
          </div>
        )}

        {/* Voice is unavailable. The Gateway usually KNOWS why, and now says so (unavailableReason).
            This screen used to hardcode a guess - "the Gateway has not made one, or this session's
            computer is offline" - which during the 2026-07-15 speech outage was false on both counts,
            and left the owner unable to tell an outage from a bug for 45 minutes. Never invent a cause
            here; render what the Gateway reported, and only fall back to the generic line when it has
            said nothing. */}
        {audioUnavailable && (
          <>
            <div className="voice-statusbar">
              <span className="voice-state voice-state-red">
                {unavailableIsServiceDown ? "Voice service down" : "Voice unavailable"}
              </span>
            </div>
            <div className="voice-narr">
              <div className="voice-narr-title">
                {unavailableIsServiceDown ? "This is not your fault." : "No narration is ready to play."}
              </div>
              <div className="voice-narr-body">
                {unavailableReason?.text
                  ? unavailableReason.text
                  : clipPhase === "error"
                    ? "The phone could not download the spoken audio for this turn. Tap Generate narration to make it again."
                    : narrative.length > 0
                      ? narrative
                      : "There is no spoken summary for this session's latest turn yet. Tap Generate narration to make one now."}
              </div>
            </div>
            {/* No button during a service outage: the Gateway is already backing off and retrying, and
                pressing Generate would hit the same dead service and fail the same way. A button that
                cannot work is worse than no button - it invites you to keep trying and blame yourself. */}
            {!unavailableIsServiceDown && (
              <button
                type="button"
                className="voice-switch"
                onClick={() => void onGenerateNow()}
                disabled={regenerating}
              >
                {regenerating ? "Generating narration..." : "Generate narration now"}
              </button>
            )}
          </>
        )}

        {/* B. WORKING - either the Wingman is reading + the phone is downloading the clip, or (when
            agentWorking) the agent has resumed and the finished-turn narration has been retired: show
            truthful copy for each so we never promise auto-play that the working gate suppresses. */}
        {working && (
          <>
            <div className="voice-statusbar">
              <span className="voice-state voice-state-yellow">
                {agentWorking ? "Agent is working..." : "Wingman is reading..."}
              </span>
            </div>
            <div className="voice-narr">
              <div className="voice-narr-title">{agentWorking ? "Working" : "Listening"}</div>
              <div className="voice-narr-body">
                {enableNote.length > 0
                  ? enableNote
                  : agentWorking
                    ? "The agent is working on the next step. The Wingman will narrate the next completed turn."
                    : "Preparing the spoken summary of the latest turn. This will play automatically."}
              </div>
            </div>
            {enableNote.length === 0 && (
              <div className="voice-working">
                <span className="voice-spinner" aria-hidden="true" />
                <span className="voice-ref">{agentWorking ? "working" : "rendering audio + downloading"}</span>
              </div>
            )}
          </>
        )}

        {/* C. SPEAKING - the audio bar and Respond are a FIXED header at the TOP (the controls you
            actually use in voice mode); the response text sits BELOW and scrolls inside its own window.
            In voice mode the text is a nice-to-have, so a long response must never push the controls
            off-screen and must never scroll up behind them - the body is a fixed-header + scrolling
            narrative column, not one big scroll (issue #1003, voice-mode screen layout). */}
        {speaking && (
          <>
            <div className="voice-top">
              <div className="voice-statusbar">
                <span className="voice-state voice-state-green">Speaking</span>
                {resumed && <span className="voice-ref">picked up where you left off</span>}
              </div>
              <div className="voice-player">
                <button
                  type="button"
                  className="voice-tri-btn"
                  onClick={onTogglePlay}
                  aria-label={playing ? "Pause" : "Play"}
                >
                  <span className={playing ? "voice-pause" : "voice-tri"} aria-hidden="true" />
                </button>
                <button
                  type="button"
                  className="voice-restart-btn"
                  onClick={onRestart}
                  aria-label="Restart from the beginning"
                >
                  <span className="voice-restart" aria-hidden="true" />
                </button>
                <input
                  type="range"
                  className="voice-seek"
                  min={0}
                  max={dur > 0 ? dur : 0}
                  step="any"
                  value={dur > 0 ? Math.min(pos, dur) : 0}
                  onChange={onSeek}
                  aria-label="Seek through the narration"
                />
                <span className="voice-ref voice-clock">{formatClock(pos)} / {formatClock(dur)}</span>
              </div>
            </div>
            <div className="voice-narr voice-narr-scroll">
              <div className="voice-narr-title">{narrative}</div>
              <div className="voice-narr-body">
                {playing ? "Speaking. Tap pause to stop, or Respond to answer." : "Tap play to listen, or Respond to answer."}
              </div>
            </div>
          </>
        )}

        {/* Regenerate response and Turn voice off used to sit here as full-width buttons below the
            text. Both are occasional, so they moved into the app bar's overflow menu (above) and the
            bottom of the screen now belongs to the two controls actually used every turn. The verbs
            themselves are unchanged: the same onGenerateNow (/wingman/explain force path) and
            onSwitchOff (ViewMode -> Text) calls. */}
      </div>

      {/* The bottom bar: the two controls the owner touches most, pinned in the thumb zone and in the
          same place on every visit. Respond appears in the speaking state only - exactly the state it
          appeared in before this layout change, so nothing about WHEN you can reply has moved. */}
      <div className="voice-bottom-bar">
        <button
          type="button"
          className="voice-snooze-btn"
          onClick={() => void manage.toggleHold()}
          disabled={manage.busy || manage.onHold === null}
        >
          {manage.held ? "Unsnooze" : "Snooze"}
        </button>
        {speaking && (
          <button type="button" className="voice-respond" onClick={() => setResponding(true)}>
            Respond
          </button>
        )}
      </div>

      {/* F. Reply: the shared dictation interface with NO Insert - Send goes straight into the
          session. There is no hold-to-talk; you tap Respond, speak, then Send. */}
      {responding && (
        <DictationDialog
          showInsert={false}
          onSend={(text) => void onRespondSend(text)}
          onSendAudio={onRespondSendAudio}
          onClose={() => setResponding(false)}
        />
      )}
    </div>
  );
}
