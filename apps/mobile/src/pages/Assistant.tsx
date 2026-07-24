import { useCallback, useLayoutEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { Link } from "react-router-dom";
import { useAssistant, type AssistantPhase } from "@devthrottle/client-core/assistant/useAssistant";

// The Assistant on the phone (fleet assistant build): the SAME fleet-level chat + voice screen the
// cockpit has, as a thin phone view over the shared client-core turn machine (useAssistant). Not tied
// to any session; every answer comes from the Gateway brain's tool calls at POST /assistant/turn.
//
// Distinct from Car Mode on purpose: Car Mode is hands-free driving (auto turn taking, end phrases,
// wake lock, full-screen chrome-less). The Assistant is hands-ON: a BUTTON is the turn - tap to talk,
// tap again to send - and chat mode types. No silence detection, no end phrase, ever.
//
// The screen is pinned to the ACTUALLY-VISIBLE viewport via --app-vh (the app-wide fit, published by
// useVisibleViewportHeight in main.tsx), so the composer and the talk button are always on-screen -
// the law every mobile session screen follows.

const PHASE_LINE: Record<AssistantPhase, string> = {
  idle: "",
  listening: "Listening... tap again when you are done.",
  transcribing: "Transcribing...",
  thinking: "Checking the fleet...",
  speaking: "Reading the answer aloud...",
};

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

export function Assistant() {
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const assistant = useAssistant(audioRef);
  const { entries, phase, mode, setMode, busy, confirmOffered } = assistant;

  const [draft, setDraft] = useState("");
  const scrollRef = useRef<HTMLDivElement | null>(null);
  const atBottomRef = useRef(true);

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
    <div className="assistant-screen">
      <div className="assistant-bar">
        <Link className="back-link" to="/">Back</Link>
        <h1>Assistant</h1>
        <div className="assistant-bar-spacer" />
        <div className="assistant-mode" role="tablist" aria-label="Assistant mode">
          <button type="button" role="tab" aria-selected={mode === "chat"}
            className={mode === "chat" ? "assistant-mode-btn active" : "assistant-mode-btn"}
            onClick={() => setMode("chat")}>Chat</button>
          <button type="button" role="tab" aria-selected={mode === "voice"}
            className={mode === "voice" ? "assistant-mode-btn active" : "assistant-mode-btn"}
            onClick={() => setMode("voice")}>Voice</button>
        </div>
      </div>

      <div className="assistant-stage">
        {entries.length === 0 ? (
          <div className="assistant-empty">
            <p>Ask anything about your fleet - no session needed:</p>
            <ul>
              <li>"How many sessions do I have open?"</li>
              <li>"Which have been open too long?"</li>
              <li>"How are we doing on credits?"</li>
              <li>"Which machines are online?"</li>
            </ul>
            <p>It can also message, snooze, start, or close sessions - closing always asks first.</p>
          </div>
        ) : (
          <div className="assistant-scroll" ref={scrollRef} onScroll={onScroll}>
            {entries.map((entry, i) => (
              entry.role === "user" ? (
                <div className="assistant-user" key={i}>{entry.text}</div>
              ) : entry.role === "error" ? (
                <div className="assistant-error" role="alert" key={i}>{entry.text}</div>
              ) : (
                <div className="assistant-turn" key={i}>
                  {(entry.actions ?? []).map((action, j) => (
                    <div className="assistant-action" key={j}>
                      <span className="assistant-dot" aria-hidden="true" />
                      <span>{action.summary}</span>
                    </div>
                  ))}
                  <div className="assistant-reply">{entry.text}</div>
                </div>
              )
            ))}
            {phase !== "idle" && <div className="assistant-phase">{PHASE_LINE[phase]}</div>}
          </div>
        )}
      </div>

      {confirmOffered && (
        <div className="assistant-confirm">
          <button type="button" className="assistant-confirm-yes" onClick={() => assistant.sendText("confirm")}>
            Yes, do it
          </button>
          <button type="button" className="assistant-confirm-no" onClick={() => assistant.sendText("cancel")}>
            Cancel
          </button>
        </div>
      )}

      {mode === "chat" ? (
        <div className="assistant-composer">
          <textarea
            className="assistant-input"
            placeholder="Ask about your fleet..."
            rows={1}
            value={draft}
            disabled={busy}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={onComposerKeyDown}
          />
          <button
            type="button"
            className={phase === "listening" ? "assistant-round listening" : "assistant-round"}
            title={phase === "listening" ? "Tap to send what you said" : "Tap to talk"}
            disabled={busy && phase !== "listening"}
            onClick={toggleTalk}
          >
            <MicGlyph />
          </button>
          <button type="button" className="assistant-round send" title="Send"
            disabled={busy || draft.trim().length === 0} onClick={send}>
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"
              strokeLinejoin="round" aria-hidden="true" focusable="false">
              <path d="M22 2L11 13" />
              <path d="M22 2l-7 20-4-9-9-4 20-7z" />
            </svg>
          </button>
        </div>
      ) : (
        <div className="assistant-talkdock">
          {phase === "speaking" && (
            <button type="button" className="assistant-stopread" onClick={assistant.stopSpeaking}>
              Stop reading
            </button>
          )}
          <button
            type="button"
            className={phase === "listening" ? "assistant-talkbtn listening" : "assistant-talkbtn"}
            disabled={busy && phase !== "listening"}
            onClick={toggleTalk}
          >
            <MicGlyph />
          </button>
          <div className="assistant-talkhint">
            {phase === "listening"
              ? "Tap again when you are done."
              : "Tap to talk - tap again when done. Replies are read aloud."}
          </div>
        </div>
      )}

      <audio ref={audioRef} className="assistant-audio" />
    </div>
  );
}
