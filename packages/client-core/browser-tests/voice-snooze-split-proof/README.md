# Phone voice-mode split Snooze proof

The Snooze button on the phone's Voice-mode session screen is one amber slab with TWO targets: a wide
part that snoozes for the user's default length (what shipped before, unchanged), and a narrow part that
opens a picker for a length chosen on the spot. This proof drives that screen in a real browser and
records what each half actually does.

Run it:

```
node run-proof.mjs
```

It writes `evidence-<date>.json` plus screenshots, and exits non-zero if any claim fails. ASCII only.

## The claims

- **A** - the slab is two targets, and the wide Snooze keeps most of the width, so the tap made every
  turn cannot land on the picker by accident. The narrow part is still a real target (at least 44 by 44).
- **B** - tapping the wide part sends NO length, so the Gateway applies the user's default, and returns
  to the queue. This is the behaviour that shipped, held in place.
- **C** - tapping the narrow part opens a sheet listing the Gateway's lengths with the default marked -
  the SAME words the Cockpit's "Snooze for" flyout and the desktop rail show, because all three read one
  shared cache through one `buildSnoozeMenu`.
- **D** - picking "4 hours" posts `snoozeMinutes=240`, not the default, and returns to the queue.
- **E** - picking a length while ALREADY snoozed re-arms the clock (`onHold=true` plus the new length).
  It never un-snoozes, which is what sharing the plain toggle would have done.
- **F** - when this phone has never successfully read the lengths, there is NO picker at all and the
  plain Snooze still works. It never invents lengths that are not the user's.

## What is real and what is not

REAL: the shipping `VoiceMode` page, its `useSessionManage` hook (optimistic hold, rollback, immediate
re-sync), the shipping `useSnoozeOptions` shared cache, the shipping `buildSnoozeMenu` decision, the
shipping `holdSession` call, and the phone app's own stylesheet served straight from source.

SIMULATED: the Gateway. A fetch shim answers the roster, the wingman voice poll and
`GET /gateway/snooze-presets`, and records every `POST /sessions/{sid}/hold` body. It has to stamp every
Gateway-owned field the client requires (`effectiveColor`, `stateLabel`, `triageBucket`), because the
client fails loud without them - so this harness cannot quietly answer with something the real Gateway
would not.

So this proves the CLIENT flow against shipping code. The snooze storage and the timer that wakes a
session are proven separately in C# by `SnoozePresetsConfigTests` and the Gateway end-to-end suite.

## Fails on purpose

Both directions were checked by breaking the code and re-running:

- dropping the length argument in `useSessionManage.holdFor` - claims D and E fail (`{"onHold":true}`
  with no `snoozeMinutes`).
- passing `null` lengths to `buildSnoozeMenu` in `VoiceMode` - the picker never appears and the run
  stops at "timed out waiting for: the lengths to load".
