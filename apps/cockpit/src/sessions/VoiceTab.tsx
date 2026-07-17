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

        {/* THE GATEWAY VERDICT, rendered verbatim (the client is dumb). This view used to derive its own
            "audio unavailable" answer and branch on retrying / service-down / reason to choose the badge,
            the message, and whether a Generate button appeared - the same guessing the mobile screen did,
            and the same dead-end Generate button it produced. All of that ruling is now folded once on the
            Gateway (VoiceDisplayFold) and arrives as v.voiceDisplay; this renders its label, tone, message,
            and a Generate button ONLY when the Gateway says one can help. */}
        {(() => {
          const vd = v.voiceDisplay;
          const busy = v.voiceOn && !v.speaking && (vd?.kind === "preparing" || vd?.kind === "working");
          const downloading = v.voiceOn && !v.speaking && vd?.kind === "ready";
          const status =
            v.voiceOn && !v.speaking && vd != null &&
            vd.kind !== "ready" && vd.kind !== "preparing" && vd.kind !== "working" && vd.kind !== "off";
          if (busy && vd) {
            return (
              <>
                <div className="voice-statusbar">
                  <span className={"voice-state voice-state-" + vd.tone}>{vd.label}</span>
                </div>
                <div className="voice-narr"><div className="voice-narr-body">{vd.message}</div></div>
                <div className="voice-working">
                  <span className="voice-spinner" aria-hidden="true" />
                  <span className="voice-ref">{vd.kind === "working" ? "working" : "rendering audio"}</span>
                </div>
              </>
            );
          }
          if (downloading) {
            return (
              <>
                <div className="voice-statusbar">
                  <span className="voice-state voice-state-yellow">Voice on its way</span>
                </div>
                <div className="voice-narr">
                  <div className="voice-narr-body">Downloading the spoken audio. It will play automatically.</div>
                </div>
                <div className="voice-working">
                  <span className="voice-spinner" aria-hidden="true" />
                  <span className="voice-ref">downloading</span>
                </div>
              </>
            );
          }
          if (status && vd) {
            return (
              <>
                <div className="voice-statusbar">
                  <span className={"voice-state voice-state-" + vd.tone}>{vd.label}</span>
                </div>
                <div className="voice-narr"><div className="voice-narr-body">{vd.message}</div></div>
                {vd.canGenerate && (
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
            );
          }
          return null;
        })()}

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
