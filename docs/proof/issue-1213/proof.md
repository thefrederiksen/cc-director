# Issue 1213 - Chat and Voice tabs via client-core hoist - proof

Phase 4 of the Cockpit improvement plan. The session page tabs are now Terminal, Chat,
Voice - built by hoisting the mobile page logic into `packages/client-core` and rendering
thin views over it in both apps, not by rewriting.

## The hoist (both apps render the shared code)

- Chat: hoisted from `apps/mobile/src/pages/Chat.tsx` into
  - `packages/client-core/src/history/chatView.ts` - pure logic: the "Show:" filter
    persistence, the change-signature, per-bubble clean + Markdown + link extraction, empty
    text. (`chatView.test.ts` - 6 new tests.)
  - `packages/client-core/src/history/useSessionChat.ts` - the live-poll hook.
  The mobile Chat page is now a thin view over these; the Cockpit `ChatTab.tsx` renders the
  same hook.
- Voice: hoisted into `packages/client-core/src/voice/useVoiceMode.ts` (plus the shared
  `voice/clips.ts` and `voice/playbackPositions.ts`, moved with `git mv`). The mobile Voice
  page and the Cockpit `VoiceTab.tsx` both render the same `useVoiceMode` hook.

## Cockpit session page (apps/cockpit/src/sessions/SessionDetail.tsx)

- Three tabs: Terminal, Chat, Voice.
- The Terminal pane stays MOUNTED (hidden) when Chat or Voice is active, so the live
  terminal WebSocket is never torn down on a tab switch.

## Verified (dev server against the live Gateway, Playwright)

- `chat-tab.png` - the Chat tab: three tabs, the "Show: Tool calls / Results / Thinking"
  filter, the LIVE marker, and the cleaned You/Assistant conversation bubbles (Markdown,
  bold, lists) - the mobile Chat page rendered at desktop width from the shared hook.
- `voice-tab-off.png` - the Voice tab off-state ("Switch to voice mode"), the mobile Voice
  screen ported through the shared hook.
- `terminal-persists-after-tab-cycle.png` - after cycling Terminal -> Chat -> Voice ->
  Terminal, the terminal shows its live content with NO "stream lost" banner (asserted in
  the script: `STREAM_LOST_PRESENT: False`) - the socket never dropped.

## Automated checks

- `tsc --noEmit` clean for client-core, cockpit, AND mobile.
- client-core `vitest run` - 154 tests pass (13 prior + 6 new chatView + the voice hoist
  kept all existing tests green).
- The mobile pages are thinned, not duplicated: the Cockpit tabs import the SAME client-core
  hooks (`useSessionChat`, `useVoiceMode`). No Director address appears in the Cockpit bundle
  (both tabs call the shared client, which uses relative Gateway URLs).

## Owner-hardware acceptance items (remote session + microphone + phone)

- On a desktop browser, a full Chat exchange with a session on a REMOTE machine (send a
  message, see the reply) - the read view and the shared composer are wired; needs a remote
  session to exercise end to end.
- A full Voice round trip with a remote session: Switch to voice mode, narration plays, a
  dictated reply is transcribed and delivered (needs a completed turn + audio + a real
  microphone). The off-state and all state cards render; the round trip needs owner hardware.
- Confirm the phone app's Chat and Voice pages behave unchanged after the hoist (mobile tsc +
  client-core tests pass and the JSX is byte-identical, but a device confirmation is ideal).
