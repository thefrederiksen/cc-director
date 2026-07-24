import { useCallback, useLayoutEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { useAssistant, type AssistantPhase } from "@devthrottle/client-core/assistant/useAssistant";
import { PageHeader } from "../components";

// The Assistant (fleet assistant build): a fleet-level chat + voice screen that is NOT tied to any
// session. It drives the Gateway brain at POST /assistant/turn - the desk surface of the same brain
// Car Mode uses - so every fact on screen came from a real Gateway tool call, never from the page.
//
// Two modes, one conversation: Chat types, Voice talks with a BUTTON (tap to talk, tap again to
// send - no silence detection, no end phrase) and reads every reply aloud. The transcript, the turn
// machine, and all Gateway calls live in client-core/assistant/useAssistant; this file is layout.

const PHASE_LINE: Record<AssistantPhase, string> = {
  idle: "",
  listening: "Listening... tap again when you are done.",
  transcribing: "Transcribing...",
  thinking: "Checking the fleet...",
  speaking: "Reading the answer aloud...",
};

/** The tap-to-talk microphone glyph, drawn on the shared 24x24 / 2px-stroke grid the rail icons use. */
function MicGlyph() {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"
      strokeLinejoin="round" aria-hidden="true" focusable="false">
      <path d="M12 2a3 3 0 0 0-3 3v7a3 3 0 0 0 6 0V5a3 3 0 0 0-3-3z" />
      <path d="M19 10v2a7 7 0 0 1-14 0v-2" />
      <path d="M12 19v3" />
    </svg>
  );
}

export function AssistantView() {
  // The permanently-mounted, hidden audio element read-aloud plays on (the mobile Voice pattern:
  // never unmounted, so a mode switch or re-render cannot orphan live audio).
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const assistant = useAssistant(audioRef);
  const { entries, phase, mode, setMode, busy, confirmOffered } = assistant;

  const [draft, setDraft] = useState("");
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const atBottomRef = useRef(true);

  // Sticky-bottom scrolling, same discipline as the session Chat tab: follow the conversation only
  // while the reader is already at the bottom.
  useLayoutEffect(() => {
    const el = scrollRef.current;
    if (el && atBottomRef.current) el.scrollTop = el.scrollHeight;
  }, [entries, phase]);

  const onScroll = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    atBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 40;
  }, []);

  const send = useCallback(() => {
    if (draft.trim().length === 0) return;
    assistant.sendText(draft);
    setDraft("");
  }, [assistant, draft]);

  const onComposerKeyDown = useCallback((e: ReactKeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      send();
    }
  }, [send]);

  const toggleTalk = useCallback(() => {
    if (phase === "listening") assistant.endTalk();
    else assistant.startTalk();
  }, [assistant, phase]);

  return (
    <div className="asst-page">
      <PageHeader
        title="Assistant"
        subtitle="Ask about your whole fleet - sessions, machines, credits, schedules. Not tied to any session."
        actions={
          <div className="asst-mode" role="tablist" aria-label="Assistant mode">
            <button type="button" role="tab" aria-selected={mode === "chat"}
              className={mode === "chat" ? "asst-mode-btn active" : "asst-mode-btn"}
              onClick={() => setMode("chat")}>Chat</button>
            <button type="button" role="tab" aria-selected={mode === "voice"}
              className={mode === "voice" ? "asst-mode-btn active" : "asst-mode-btn"}
              onClick={() => setMode("voice")}>Voice</button>
          </div>
        }
      />

      <div className="asst-stage">
        {entries.length === 0 ? (
          <div className="asst-empty">
            <p>Ask anything about your development fleet:</p>
            <ul>
              <li>"How many sessions do I have open, and is anything stuck?"</li>
              <li>"Which sessions have been open too long?"</li>
              <li>"How are we doing on credits?"</li>
              <li>"Which machines are online?"</li>
              <li>"What runs automatically tonight?"</li>
            </ul>
            <p>It can also act - message a session, snooze one, start one, or close one (closing always
              asks you to confirm first).</p>
          </div>
        ) : (
          <div className="asst-scroll" ref={scrollRef} onScroll={onScroll}>
            {entries.map((entry, i) => (
              entry.role === "user" ? (
                <div className="asst-user" key={i}>{entry.text}</div>
              ) : entry.role === "error" ? (
                <div className="asst-error" role="alert" key={i}>{entry.text}</div>
              ) : (
                <div className="asst-turn" key={i}>
                  {(entry.actions ?? []).map((action, j) => (
                    <div className="asst-action" key={j}>
                      <span className="asst-dot" aria-hidden="true" />
                      <span className="asst-action-text">{action.summary}</span>
                    </div>
                  ))}
                  <div className="asst-reply">{entry.text}</div>
                </div>
              )
            ))}
            {phase !== "idle" && <div className="asst-phase">{PHASE_LINE[phase]}</div>}
          </div>
        )}
      </div>

      {confirmOffered && (
        <div className="asst-confirm">
          <button type="button" className="asst-confirm-yes" onClick={() => assistant.sendText("confirm")}>
            Yes, do it
          </button>
          <button type="button" className="asst-confirm-no" onClick={() => assistant.sendText("cancel")}>
            Cancel
          </button>
        </div>
      )}

      {mode === "chat" ? (
        <div className="asst-composer">
          <textarea
            className="asst-input"
            placeholder="Ask about your fleet..."
            rows={1}
            value={draft}
            disabled={busy}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={onComposerKeyDown}
          />
          <button
            type="button"
            className={phase === "listening" ? "asst-round listening" : "asst-round"}
            title={phase === "listening" ? "Tap to send what you said" : "Tap to talk"}
            disabled={busy && phase !== "listening"}
            onClick={toggleTalk}
          >
            <MicGlyph />
          </button>
          <button type="button" className="asst-round send" title="Send" disabled={busy || draft.trim().length === 0}
            onClick={send}>
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"
              strokeLinejoin="round" aria-hidden="true" focusable="false">
              <path d="M22 2L11 13" />
              <path d="M22 2l-7 20-4-9-9-4 20-7z" />
            </svg>
          </button>
        </div>
      ) : (
        <div className="asst-talkdock">
          {phase === "speaking" && (
            <button type="button" className="asst-stopread" onClick={assistant.stopSpeaking}>
              Stop reading
            </button>
          )}
          <button
            type="button"
            className={phase === "listening" ? "asst-talkbtn listening" : "asst-talkbtn"}
            disabled={busy && phase !== "listening"}
            onClick={toggleTalk}
          >
            <MicGlyph />
          </button>
          <div className="asst-talkhint">
            {phase === "listening"
              ? "Tap again when you are done."
              : "Tap to talk - tap again when done. Replies are read aloud."}
          </div>
        </div>
      )}

      {/* Read-aloud plays here; never unmounted (see useAssistant). */}
      <audio ref={audioRef} className="asst-audio" />
    </div>
  );
}
