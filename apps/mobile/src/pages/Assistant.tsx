import { useCallback, useLayoutEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { Link } from "react-router-dom";
import { useAssistant, type AssistantMode, type AssistantPhase } from "@devthrottle/client-core/assistant/useAssistant";
import { DictationDialog } from "@devthrottle/client-core/dictation/DictationDialog";
import { joinText } from "@devthrottle/client-core/dictation/transcript";
import { StatusPill } from "../components/StatusPill";

// The Assistant on the phone (fleet assistant build): the SAME fleet-level chat + voice screen the
// cockpit has, as a thin phone view over the shared client-core turn machine (useAssistant). Not tied
// to any session; every answer comes from the Gateway brain's tool calls at POST /assistant/turn.
//
// Distinct from Car Mode on purpose: Car Mode is hands-free driving (auto turn taking, end phrases,
// wake lock, full-screen chrome-less). The Assistant is hands-ON: you press to speak and press Send
// when you are done, and chat mode types. No silence detection, no end phrase, ever.
//
// Speaking a question is the SHARED DICTATION DIALOG (DictationDialog in client-core) - the same
// recorder the Terminal, Chat and Voice-mode Respond flows open, with equalizer bars, an elapsed
// timer, a Pause checkpoint, an editable transcript, and Cancel / Send. It replaced a bare round
// microphone button that just turned red: no level meter, no timer, and no way to back out of a
// recording once it had started. Its Send text goes straight into sendText().
//
// The screen is pinned to the ACTUALLY-VISIBLE viewport via --app-vh (the app-wide fit, published by
// useVisibleViewportHeight in main.tsx), so the composer and the talk button are always on-screen -
// the law every mobile session screen follows.
//
// The mode toggle is CENTRED, never in the top-right corner. That corner is not this screen's to
// spend: the fixed network status pill (.net-pill) is pinned there on every screen that does not give
// it a home of its own, and it sat directly on top of the toggle - a green pill over the one control
// that decides how the whole screen behaves. This screen now gives the pill a real home inline in its
// own bar (the Home and session-screen pattern; the fixed one stands down for /assistant in
// main.tsx), and the toggle owns the middle.

const PHASE_LINE: Record<AssistantPhase, string> = {
  idle: "",
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

/** The one Chat / Voice control, rendered small on its own centred row and BIG in the empty state -
 *  where the screen has nothing else to say and picking how to ask is the only thing to do, so it is
 *  worth a wide tap target in the middle instead of a 13px pill up in the chrome. */
function ModeToggle({ mode, setMode, large = false }: {
  mode: AssistantMode;
  setMode: (mode: AssistantMode) => void;
  large?: boolean;
}) {
  return (
    <div className={large ? "assistant-mode assistant-mode-large" : "assistant-mode"} role="tablist"
      aria-label="Assistant mode">
      <button type="button" role="tab" aria-selected={mode === "chat"}
        className={mode === "chat" ? "assistant-mode-btn active" : "assistant-mode-btn"}
        onClick={() => setMode("chat")}>Chat</button>
      <button type="button" role="tab" aria-selected={mode === "voice"}
        className={mode === "voice" ? "assistant-mode-btn active" : "assistant-mode-btn"}
        onClick={() => setMode("voice")}>Voice</button>
    </div>
  );
}

export function Assistant() {
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const assistant = useAssistant(audioRef);
  const { entries, phase, mode, setMode, busy, confirmOffered } = assistant;

  const [draft, setDraft] = useState("");
  // Whether the shared dictation dialog is open. It owns the microphone the whole time it is up.
  const [dictating, setDictating] = useState(false);
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

  // Insert (chat mode only): the dictated words land in the composer to edit instead of being asked
  // straight away. joinText is the one transformation allowed on dictated words.
  const onDictateInsert = useCallback((text: string) => {
    setDraft((cur) => joinText(cur, text));
  }, []);

  return (
    <div className="assistant-screen">
      <div className="assistant-bar">
        <Link className="back-link" to="/">Back</Link>
        <h1>Assistant</h1>
        <div className="assistant-bar-spacer" />
        {/* The network pill's home on this screen - inline, so the fixed overlay stands down and
            nothing lands on top of the mode toggle. */}
        <StatusPill inline />
      </div>

      {/* Once a conversation exists the toggle is chrome: small, centred, clear of both corners. In
          the empty state it is the main event and renders big in the middle of the stage instead. */}
      {entries.length > 0 && (
        <div className="assistant-moderow">
          <ModeToggle mode={mode} setMode={setMode} />
        </div>
      )}

      <div className="assistant-stage">
        {entries.length === 0 ? (
          <div className="assistant-empty">
            <div className="assistant-modepick">
              <p className="assistant-modepick-label">How do you want to ask?</p>
              <ModeToggle mode={mode} setMode={setMode} large />
              <p className="assistant-modepick-hint">
                {mode === "chat"
                  ? "Type it, or press the microphone to dictate. Replies stay quiet."
                  : "Press the microphone, speak, then Send. Replies are read aloud."}
              </p>
            </div>
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
            className="assistant-round"
            title="Dictate your question"
            aria-label="Dictate your question"
            disabled={busy}
            onClick={() => setDictating(true)}
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
            className="assistant-talkbtn"
            title="Dictate your question"
            aria-label="Dictate your question"
            disabled={busy}
            onClick={() => setDictating(true)}
          >
            <MicGlyph />
          </button>
          <div className="assistant-talkhint">
            Press to speak - Pause to check the words, Send to ask. Replies are read aloud.
          </div>
        </div>
      )}

      {/* The shared dictation dialog owns the microphone while it is open. Send hands the finished
          text to the turn machine; Insert (chat mode only) drops it in the composer to edit first.
          onSendAudio is deliberately NOT wired: the answer appears on THIS screen, so there is
          nothing to release the screen for - the same reason the Cockpit composer leaves it unwired. */}
      {dictating && (
        <DictationDialog
          showInsert={mode === "chat"}
          onInsert={onDictateInsert}
          onSend={(text) => assistant.sendText(text)}
          onClose={() => setDictating(false)}
        />
      )}

      <audio ref={audioRef} className="assistant-audio" />
    </div>
  );
}
