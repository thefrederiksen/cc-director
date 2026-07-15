# Cockpit snooze menu proof

Drives the REAL Cockpit session menu in a real Chromium against a fake Gateway. Run it from this
directory:

```
node run-proof.mjs
```

It prints PASS or FAIL per claim, exits non-zero on any failure, and writes `evidence-<date>.json` plus
screenshots beside it.

## What is real and what is not

REAL: the shipping `SessionMenu` component, the shipping `useSnoozeOptions` shared cache, the shipping
`buildSnoozeMenu` decision, the shipping `holdSession` call, and the Cockpit's own `styles.css` (served
straight from source, so the screenshot cannot drift from what ships).

SIMULATED: the Gateway. An in-page fetch shim answers `GET /gateway/snooze-presets` and records every
`POST /sessions/{sid}/hold` body, applying the hold to its own state so the page re-renders from the
server's answer rather than from local optimism.

This proves the CLIENT flow. The Gateway's storage, validation, and the real snooze timer are proven
separately: in C# by `SnoozePresetsConfigTests` and the Gateway end-to-end suite, and live against a real
Gateway plus Director for the desktop menu.

## The claims

- **A** - the plain item names the user's default length, from the Gateway ("Snooze  (1 hour)").
- **B** - "Snooze for" opens the Gateway's four lengths with the default marked - the SAME words the
  desktop menu shows (pinned on both sides: `snoozeMenu.test.ts` and `SnoozeMenuModelTests.cs`).
- **C** - picking "4 hours" POSTs `snoozeMinutes=240`, not the default.
- **D** - the plain Snooze click sends NO length, so the Gateway applies the default.
- **E** - a snoozed session says "Unsnooze" and STILL offers "Snooze for", so a length changes in one
  step instead of unsnooze-then-snooze-again.
- **F** - three menus on one page share ONE presets fetch. A fetch per rail card would make opening a
  menu slow and chatty.
- **G** - the flyout is fully on screen for a card at the LEFT edge.

## Three real bugs this harness caught

Worth keeping, because each one made the lengths unclickable and none would have shown up in a unit test:

1. **The flyout closed as you reached for it.** It is portaled, so it is not a DOM child of "Snooze for":
   moving the pointer toward it fired `mouseleave` on the item and unmounted it mid-travel. Fixed with
   hover intent - a leave schedules the close, an enter on either side cancels it.
2. **The flyout opened off-screen.** It prefers to open left (the parent is right-anchored), but a card
   near the LEFT edge has no room there, so it landed at a negative x. Fixed with a left/right flip, the
   same way the parent flips up when the bottom will not fit. Claim G pins it.
3. **The outside-click handler ate the click.** It checked `rootRef` and `popRef`, but the flyout is a
   THIRD portal on `document.body` - so `mousedown` on a length counted as an outside click and tore the
   menu down before the button's `click` could fire. Fixed by teaching the handler about `subPopRef`.
