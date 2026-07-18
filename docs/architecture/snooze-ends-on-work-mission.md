# Mission: Working ends a snooze, completely

**Status:** ACTIVE (seeded 2026-07-17)
**Branch:** `mission/snooze-ends-on-work`
**Worktree:** `D:\ReposFred\devthrottle-snooze-work`
**Architect session:** "Snooze Ends On Work - Architect" (d1ee2299)
**Conduct:** governed entirely by `.claude/skills/mission/SKILL.md`. This brief describes the WORK
only. It grants nothing and restates no rule.

---

## THE WHY (front and centre)

Snooze is a **human** act. The owner, in his words: *"If you snooze it, it's a human thing I'm
doing. As soon as there's any work on that terminal that comes out of snooze, period, full stop."*

Today it does not work that way. The owner snoozed a session ("Wingman menu handling plan"); another
agent sent it a fleet message; it did work, finished - and it is **snoozed again right now**. That is
wrong. Snooze exists to quiet a session that has *nothing happening*. The instant there is activity
on that terminal, the snooze is over and must be gone, so that when the work ends the session comes
back as a normal **red "needs you"** - not silently re-parked where the owner will never look.

There is a second, linked wrong. When a session returns from snooze there is a little **"Snooze
ended"** badge, whose whole job is to tell the owner *"this is not a fresh message - it came back
because its timer ran out, go see why it went quiet."* That badge is a one-way latch: it never
clears. So it rides along on sessions that were re-snoozed, and it would ride along on a session that
came back via **work**. It must only ever appear on a session that genuinely returned to needs-you
**because its snooze timer expired**. Work must clear it too.

**True when this is finished:**
- Any `Working`/`Starting` activity on a snoozed session **deletes** its snooze outright. When the
  work settles, the session reads red "needs you", never grey "Snoozed".
- The "Snooze ended" badge appears **only** when a session returned to needs-you by timer expiry. A
  re-snooze clears it; a work burst clears it.
- `docs/new_architecture/session-state.html` describes the machine as it actually is.

---

## Design decisions already made (rulings)

All four below are **owner-stated** unless marked inferred.

1. **Working deletes an ARMED snooze.** (owner-stated) Not "outranks visually" - deletes the registry
   entry. The state document already states this law ("Working knocks a session out of snooze.
   Always... the snooze timer dies with it"); the code stopped honouring it when snooze ownership
   moved to the Gateway.

2. **A DEFERRED snooze still defers - working does NOT clear it.** (inferred, but forced by the
   document and by logic) "Snooze me when this finishes" is requested *while working*; if the next
   working observation cleared it, an agent could never snooze its own session. So: the working edge
   clears `Held` (armed), and leaves `DeferredHold` alone. This distinction is load-bearing - do not
   collapse it.

3. **The "Snooze ended" badge (`SnoozeExpired`) is owned by the fold, both ways.** (owner-stated
   intent; mechanism inferred) Assign `s.SnoozeExpired = snoozeRegistry.IsExpired(sid, now)` - not
   OR-in. Combined with decision 1, a work burst deletes the entry, so `IsExpired` is false, so the
   badge clears. The badge then means exactly one thing: returned-to-needs-you-by-expiry.

4. **Background-quiet is a SEPARATE, FUTURE mechanism - OUT of scope here.** (owner-stated) The owner
   accepts that agents churning quietly in the background is a real need, but it must NOT be solved by
   letting a snooze survive through work. That is a distinct "background/running" state, a later
   mission. Do not build it here and do not weaken decision 1 to approximate it.

---

## The work, in the order it lands

Slice per the conduct file: one pull request per coherent piece, fix and its regression guard in the
SAME slice, each fix proven able to fail (revert -> watch red with the reported symptom -> restore).

**Slice 1 - Working deletes an armed snooze (the Gateway edge).**
- In `src/CcDirector.Gateway/Snooze/SnoozeLandingObserver.cs` `Observe(...)`, add a **Working edge**:
  when the pushed session's activity is `Working`/`Starting` and the registry holds an **armed**
  (not deferred) entry for it, `Clear` it and mirror `HoldStates.None` down to the Director. This is
  the deliberate reversal of the current "Deliberately NOT an edge: Working" comment - rewrite that
  comment to state the owner's law and why (a snooze is the owner's "not now"; any work means the
  thing is alive, so the snooze is spent).
- Preserve decision 2: a `DeferredHold` entry is untouched by this edge; only `Land`/settle converts
  it. Add/keep a test that snoozing-while-working still defers and lands correctly.
- Tests: a snoozed (armed) session that starts working has its entry gone; a deferred entry survives
  a working push; the owner-turn/exit/expiry paths still behave. Revert-prove each.

**Slice 2 - The "Snooze ended" badge clears both ways (the fold).**
- In `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` (the fold, ~line 2647), change the one-way
  `if (IsExpired) s.SnoozeExpired = true;` to an unconditional **assignment**
  `s.SnoozeExpired = snoozeRegistry.IsExpired(sid, nowUtc);` and correct the "SnoozeExpired stays..."
  comment above it, which is wrong (dropping the entry never wrote false either).
- Verify the cleared value rides the whole way: the down-stamp to the Director
  (`GatewaySnoozeExpired`, `FleetDisplayStateObserver`), `GET /sessions/{sid}`, `/exes/list`, the
  Cockpit and mobile rosters. The clients are dumb and already render the field verbatim - no client
  change should be needed; confirm that.
- Tests: a re-snooze after expiry clears the badge; a work burst clears the badge; a genuine expiry
  still shows it. This subsumes the bug recorded in memory `snooze-expired-never-cleared-gateway-bug`.

**Slice 3 - Reconcile the state document.**
- `docs/new_architecture/session-state.html`: the working-clears-snooze law is already stated - but
  the mechanics table still credits the deleted Director-side `RequestHold`/working-edge lift. Rewrite
  those rows to say the **Gateway** owns the working edge now. Add the SnoozeExpired-cleared-by-work
  rule. Resolve the "belt and braces disagree" passage. State plainly that background-quiet is a
  separate mechanism, not snooze surviving work.

Landing order: 1, then 2, then 3. Slices 1 and 2 are independent in code but 2's badge only becomes
fully correct once 1 deletes the entry, so land 1 first.

---

## Out of scope (do not invent)

- The background-quiet / "let it churn silently" mechanism (decision 4). Future mission.
- Any client-side logic change. Clients are dumb and render Gateway-stamped fields (CLAUDE.md rule 7).
  If a client needs a code change to make this work, that is a finding to surface, not a licence to
  add ruling to a client.
- Snooze length, presets, the snooze menu, or the deferred-clock-start behaviour (defect 20) - all
  correct and untouched.
- Reworking how snooze ownership sits on the Gateway. It is right that the Gateway owns it; this
  mission restores one missing edge, it does not re-litigate the architecture.

---

## Verification bar (for the QA report)

The mission is not done on green tests. Prove it live: with the Gateway running, snooze a session,
send it a fleet message so it works, and watch it come back **red "needs you" with no "Snooze ended"
badge**. Separately, let a real snooze timer expire and confirm the badge **does** appear. The owner
is bothered once, at the end, with that report.

## Conduct

Governed by `.claude/skills/mission/SKILL.md`. Architect settles design and is the only one who lands
on `main`; one Manager per phase, killed when its phase is done; a **different-family** Inspector
(Codex) reviews to a FILE before anything merges; merged to `origin/main` is the only "done".
