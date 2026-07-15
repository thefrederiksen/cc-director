import { useVoiceMode, formatClock } from "@devthrottle/client-core/voice/useVoiceMode";
import { DictationDialog } from "@devthrottle/client-core/dictation/DictationDialog";

// The Cockpit Voice tab (issue #1213): a thin view over the SAME shared client-core hook the mobile
// Voice page uses (useVoiceMode), so the two apps render the hands-free Wingman narration from one
// source. The states are the mobile states unchanged - off, working, speaking, "voice unavailable" -
// using the ported voice-* classes; only the layout is adapted for a wide screen (a centered column).
// The Respond flow is the shared DictationDialog with no Insert, Send goes straight into the session.

export function VoiceTab({ sessionId }: { sessionId: string | undefined }) {
  const v = useVoiceMode(sessionId);

  return (
    <div className="voice-tab">
      {/* The clip element is always mounted (hidden) so auto-play works in any state; the visible play
          controls live in the speaking state below. */}
      <audio
        ref={v.setAudioEl}
        src={v.clipUrl ?? undefined}
        preload="auto"
        onLoadedMetadata={v.onLoadedMeta}
        onTimeUpdate={v.onTimeUpdate}
        onPlay={() => v.setPlaying(true)}
        onPause={() => v.setPlaying(false)}
        onEnded={v.onEndedAudio}
        style={{ display: "none" }}
      />

      <div className="voice-body">
        {v.error !== null && (
          <div className="composer-error" role="alert">{v.error}</div>
        )}

        {/* A. OFF - one clear "Switch to voice mode" button; only ever shown once a poll has confirmed
            the session is NOT in voice mode. */}
        {!v.voiceOn && v.pollDone && (
          <div className="voice-off">
            <p className="voice-off-title">Voice mode is off for this session.</p>
            <p className="voice-hint">Turn it on and the Wingman will start narrating every turn.</p>
            <button type="button" className="voice-switch" onClick={() => void v.onSwitchOn()} disabled={v.enabling}>
              {v.enabling ? "Switching..." : "Switch to voice mode"}
            </button>
          </div>
        )}

        {/* Render the reason the GATEWAY gave (v.unavailableReason), never a guess. This block used to
            hardcode "the Gateway has not made one, or this session's computer is offline" - the exact
            string the mobile screen had, duplicated character-for-character, and false on both counts
            during the 2026-07-15 speech outage. Both views now read the shared hook, so they cannot
            drift apart again; the fallback line is only for when the Gateway has said nothing. */}
        {v.audioUnavailable && (
          <>
            <div className="voice-statusbar">
              <span className="voice-state voice-state-red">
                {v.unavailableIsServiceDown ? "Voice service down" : "Voice unavailable"}
              </span>
            </div>
            <div className="voice-narr">
              <div className="voice-narr-title">
                {v.unavailableIsServiceDown ? "This is not your fault." : "No narration is ready to play."}
              </div>
              <div className="voice-narr-body">
                {v.unavailableReason?.text
                  ? v.unavailableReason.text
                  : v.clipPhase === "error"
                    ? "The browser could not download the spoken audio for this turn. Click Generate narration to make it again."
                    : v.narrative.length > 0
                      ? v.narrative
                      : "There is no spoken summary for this session's latest turn yet. Click Generate narration to make one now."}
              </div>
            </div>
            {/* No button while the service is down: it hits the same dead service and fails the same
                way, and the Gateway is already backing off and retrying on its own. */}
            {!v.unavailableIsServiceDown && (
              <button
                type="button"
                className="voice-switch"
                onClick={() => void v.onGenerateNow()}
                disabled={v.regenerating}
              >
                {v.regenerating ? "Generating narration..." : "Generate narration now"}
              </button>
            )}
          </>
        )}

        {/* B. WORKING - the Wingman is reading + the browser is downloading the clip, or the agent has
            resumed and the finished-turn narration has been retired. */}
        {v.working && (
          <>
            <div className="voice-statusbar">
              <span className="voice-state voice-state-yellow">
                {v.agentWorking ? "Agent is working..." : "Wingman is reading..."}
              </span>
            </div>
            <div className="voice-narr">
              <div className="voice-narr-title">{v.agentWorking ? "Working" : "Listening"}</div>
              <div className="voice-narr-body">
                {v.enableNote.length > 0
                  ? v.enableNote
                  : v.agentWorking
                    ? "The agent is working on the next step. The Wingman will narrate the next completed turn."
                    : "Preparing the spoken summary of the latest turn. This will play automatically."}
              </div>
            </div>
            {v.enableNote.length === 0 && (
              <div className="voice-working">
                <span className="voice-spinner" aria-hidden="true" />
                <span className="voice-ref">{v.agentWorking ? "working" : "rendering audio + downloading"}</span>
              </div>
            )}
          </>
        )}

        {/* C. SPEAKING - the audio bar and Respond are pinned at the top; the response text scrolls
            below. */}
        {v.speaking && (
          <>
            <div className="voice-top">
              <div className="voice-statusbar">
                <span className="voice-state voice-state-green">Speaking</span>
                {v.resumed && <span className="voice-ref">picked up where you left off</span>}
              </div>
              <div className="voice-player">
                <button
                  type="button"
                  className="voice-tri-btn"
                  onClick={v.onTogglePlay}
                  aria-label={v.playing ? "Pause" : "Play"}
                >
                  <span className={v.playing ? "voice-pause" : "voice-tri"} aria-hidden="true" />
                </button>
                <button
                  type="button"
                  className="voice-restart-btn"
                  onClick={v.onRestart}
                  aria-label="Restart from the beginning"
                >
                  <span className="voice-restart" aria-hidden="true" />
                </button>
                <input
                  type="range"
                  className="voice-seek"
                  min={0}
                  max={v.dur > 0 ? v.dur : 0}
                  step="any"
                  value={v.dur > 0 ? Math.min(v.pos, v.dur) : 0}
                  onChange={v.onSeek}
                  aria-label="Seek through the narration"
                />
                <span className="voice-ref voice-clock">{formatClock(v.pos)} / {formatClock(v.dur)}</span>
              </div>
              <button type="button" className="voice-respond" onClick={() => v.setResponding(true)}>
                Respond
              </button>
            </div>
            <div className="voice-narr voice-narr-scroll">
              <div className="voice-narr-title">{v.narrative}</div>
              <div className="voice-narr-body">
                {v.playing ? "Speaking. Click pause to stop, or Respond to answer." : "Click play to listen, or Respond to answer."}
              </div>
            </div>
          </>
        )}

        {/* Turn voice off: a low-emphasis control present in every on-state. */}
        {v.voiceOn && (
          <button type="button" className="voice-off-toggle" onClick={() => void v.onSwitchOff()}>
            Turn voice off
          </button>
        )}
      </div>

      {/* Reply: the shared dictation interface with NO Insert - Send goes straight into the session. */}
      {v.responding && (
        <DictationDialog
          showInsert={false}
          onSend={(text) => void v.onRespondSend(text)}
          onSendAudio={v.onRespondSendAudio}
          onClose={() => v.setResponding(false)}
        />
      )}
    </div>
  );
}
