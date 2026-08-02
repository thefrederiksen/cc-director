import { useCallback, useLayoutEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from "react";
import { useAssistant, type AssistantMode, type AssistantPhase } from "@devthrottle/client-core/assistant/useAssistant";
import { DictationDialog } from "@devthrottle/client-core/dictation/DictationDialog";
import { joinText } from "@devthrottle/client-core/dictation/transcript";
import { PageHeader } from "../components";

// The Assistant (fleet assistant build): a fleet-level chat + voice screen that is NOT tied to any
// session. It drives the Gateway brain at POST /assistant/turn - the desk surface of the same brain
// Car Mode uses - so every fact on screen came from a real Gateway tool call, never from the page.
//
// Two modes, one conversation: Chat types, Voice talks. Speaking is the SHARED DICTATION DIALOG
// (DictationDialog in client-core) - the same recorder the session composer's Speak button opens,
// with equalizer bars, an elapsed timer, a Pause checkpoint, an editable transcript, and Cancel /
// Send. It replaced a bare round microphone button that just turned red: no level meter, no timer,
// and no way to back out once it was recording. Voice mode also reads every reply aloud.
//
// The transcript, the turn machine, and all Gateway calls live in client-core/assistant/useAssistant;
// this file is layout.

const PHASE_LINE: Record<AssistantPhase, string> = {
  idle: "",
  thinking: "Checking the fleet...",
  speaking: "Reading the answer aloud...",
};

/** The dictate microphone glyph, drawn on the shared 24x24 / 2px-stroke grid the rail icons use. */
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

/** The one Chat / Voice control, rendered small in the page header and BIG in the middle of the empty
 *  state - where nothing has been asked yet, so picking how to ask is the only thing to do on the
 *  screen and it is worth a real target instead of a 13px pill up in the chrome. */
function ModeToggle({ mode, setMode, large = false }: {
  mode: AssistantMode;
  setMode: (mode: AssistantMode) => void;
  large?: boolean;
}) {
  return (
    <div className={large ? "asst-mode asst-mode-large" : "asst-mode"} role="tablist"
      aria-label="Assistant mode">
      <button type="button" role="tab" aria-selected={mode === "chat"}
        className={mode === "chat" ? "asst-mode-btn active" : "asst-mode-btn"}
        onClick={() => setMode("chat")}>Chat</button>
      <button type="button" role="tab" aria-selected={mode === "voice"}
        className={mode === "voice" ? "asst-mode-btn active" : "asst-mode-btn"}
        onClick={() => setMode("voice")}>Voice</button>
    </div>
  );
}

export function AssistantView() {
  // The permanently-mounted, hidden audio element read-aloud plays on (the mobile Voice pattern:
  // never unmounted, so a mode switch or re-render cannot orphan live audio).
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const assistant = useAssistant(audioRef);
  const { entries, phase, mode, setMode, busy, confirmOffered } = assistant;

  const [draft, setDraft] = useState("");
  // Whether the shared dictation dialog is open. It owns the microphone the whole time it is up.
  const [dictating, setDictating] = useState(false);
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

  // Insert (chat mode only): the dictated words land in the composer to edit instead of being asked
  // straight away. joinText is the one transformation allowed on dictated words.
  const onDictateInsert = useCallback((text: string) => {
    setDraft((cur) => joinText(cur, text));
  }, []);

  return (
    <div className="asst-page">
      <PageHeader
        title="Assistant"
        subtitle="Ask about your whole fleet - sessions, machines, credits, schedules. Not tied to any session."
        actions={entries.length > 0 ? <ModeToggle mode={mode} setMode={setMode} /> : undefined}
      />

      <div className="asst-stage">
        {entries.length === 0 ? (
          <div className="asst-empty">
            {/* Nothing asked yet: the mode choice is the screen's one decision, so it sits big and
                centred above the examples instead of as a small pill in the header. */}
            <div className="asst-modepick">
              <p className="asst-modepick-label">How do you want to ask?</p>
              <ModeToggle mode={mode} setMode={setMode} large />
              <p className="asst-modepick-hint">
                {mode === "chat"
                  ? "Type it, or press the microphone to dictate. Replies stay quiet."
                  : "Press the microphone, speak, then Send. Replies are read aloud."}
              </p>
            </div>
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
            className="asst-round"
            title="Dictate your question"
            aria-label="Dictate your question"
            disabled={busy}
            onClick={() => setDictating(true)}
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
            className="asst-talkbtn"
            title="Dictate your question"
            aria-label="Dictate your question"
            disabled={busy}
            onClick={() => setDictating(true)}
          >
            <MicGlyph />
          </button>
          <div className="asst-talkhint">
            Press to speak - Pause to check the words, Send to ask. Replies are read aloud.
          </div>
        </div>
      )}

      {/* The shared dictation dialog owns the microphone while it is open. Send hands the finished
          text to the turn machine; Insert (chat mode only) drops it in the composer to edit first.
          onSendAudio is deliberately NOT wired - the answer appears on THIS screen, so there is
          nothing to release the screen for (the session composer leaves it unwired for the same
          reason, issue #1210's fix). */}
      {dictating && (
        <DictationDialog
          surface="cockpit"
          showInsert={mode === "chat"}
          onInsert={onDictateInsert}
          onSend={(text) => assistant.sendText(text)}
          onClose={() => setDictating(false)}
        />
      )}

      {/* Read-aloud plays here; never unmounted (see useAssistant). */}
      <audio ref={audioRef} className="asst-audio" />
    </div>
  );
}
