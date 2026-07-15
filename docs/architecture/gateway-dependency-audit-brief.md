# Manager brief - the Gateway-dependency audit

**Architect:** the session named `Session States - Architect`. Report to it, never to the owner.
**How you conduct yourself:** [`.claude/skills/mission/SKILL.md`](../../.claude/skills/mission/SKILL.md) - the run-book. Read it first. This brief does not restate it and grants nothing.
**Your branch:** `mission/gateway-dependency-audit`. **Your worktree:** `D:\ReposFred\devthrottle-audit`.

---

## THE WHY

**The owner takes a laptop somewhere it cannot reach the Gateway machine, and DevThrottle has to still
work.** In his words: *"if I'm on a laptop and I can't access the gateway computer, it should still run at
least."*

He asked this because he suspects we have drifted. Feature by feature, over months, capability has moved
to the Gateway for good reasons each time - the Gateway owns state, owns snooze timing, owns transcription,
hands out session numbers - and **nobody has ever asked what the sum of those decisions costs a man on a
train.** Each one was defensible alone. He wants to know what they add up to.

**This is the question you are answering: WHAT DOES A DIRECTOR LOSE WHEN IT CANNOT REACH ITS GATEWAY?**

Not "is the Gateway good" - it is, and this is not a proposal to undo it. The question is whether the
degraded state is one he can work in, and whether the places it degrades are the places we *chose* or the
places we *drifted*.

---

## THIS IS AN AUDIT. YOU BUILD NOTHING.

The deliverable is a document. No fixes, no refactors, no "while I was in there". If you find something
appalling, WRITE IT DOWN and tell the Architect - do not fix it. A fix hides inside an audit and nobody
reviews it properly.

The model for the shape and the standard is
[`gap-4-desktop-asks-recommendation.md`](gap-4-desktop-asks-recommendation.md) - read it before you start.
It investigates, states a mechanism from cited code, names its own uncertainty, gives the owner a real
decision, and builds nothing. It is also the paper that had to **withdraw its own fabricated number**, and
that is the most useful thing in it: read that section twice.

---

## What the Architect already found - VERIFY IT, do not inherit it

Spot-checks only, from `origin/main`. Every one could be wrong or incomplete. Treat them as leads, not
findings, and say so in the document if one turns out to be false.

| Capability | What the Architect thinks | Where he looked |
|---|---|---|
| Spawning/running sessions | **Works offline.** SessionManager has no Gateway reference at all | `src/CcDirector.Core/Sessions/SessionManager.cs` |
| The rail's colours | **Works offline.** Folds `ControlEndpoints.Map(session)` in-process | `SessionViewModel.FoldInput` |
| Session numbers | **Falls back.** A null/failed answer yields a local offline number | `ControlApiHost.cs:474-482` |
| **Snooze** | **BROKEN offline, deliberately.** "You need to be connected to a Gateway to use snooze" | `MainWindow.axaml.cs:2185-2195` |
| **Voice / transcription** | **BROKEN offline, by design.** One path, through the Gateway | the transcription law |

---

## The distinction that matters most, and where the bugs will be

**"No Gateway configured" and "Gateway configured but unreachable" are DIFFERENT STATES, and code that
conflates them is where this audit will find its real defects.**

The owner's case is the second one: his laptop has a Gateway configured. It is simply not there.

Concrete lead: `ControlApiHost.cs:482` sets `FleetNumberingActive = gatewayConfig.IsEnabled`. **Enabled is
not reachable.** The numbering path looks like it handles the failure anyway - but that shape (`IsEnabled`
standing in for "the Gateway will answer") is exactly what to hunt for everywhere else. Every place that
asks "is a Gateway configured?" and means "will a Gateway answer?" is a candidate.

For each finding, say WHICH state breaks it: unconfigured, unreachable, or both.

---

## How to search - the sweep, not the guess

Do not reason about what "probably" needs the Gateway. Find it.

- Start from the seams: `GatewayClient`, `GatewayMonitor`, `GatewayHold`, `IsEnabled`, `GatewayConnectionStatus`, `_gatewayClient`, `FleetNumber*`, the tunnel/stream client.
- Then go the OTHER way: walk the desktop's user-facing surfaces - every button, menu item and panel in `MainWindow`, `FifoWindow`, the dialogs - and ask of each "does this still work on the train?" The seam-first search finds what CALLS the Gateway; the surface-first search finds what the OWNER loses. **You need both, and they will not produce the same list.**
- **A call site is not a caller.** Prove what runs, not what exists.
- **Never state a cause you have not observed.** If you cannot tell whether something degrades, say "undiagnosed, here is how to find out."

---

## The deliverable

`docs/architecture/gateway-dependency-audit.md`. Owner-facing, plain English, no abbreviations.

1. **The one-line answer**, first: can he work on that laptop, yes or no, and what does it feel like.
2. **A table**: capability | works offline? | which state breaks it (unconfigured / unreachable / both) | the file and line that decides it.
3. **The three categories, separated** - this is the spine of the document:
   - **CHOSEN**: degrades because someone decided it should, with the reason findable. Snooze looks like this: it is Gateway-owned because a local snooze had no timer, and the no-fallback rule says fail loudly rather than hand him a snooze that silently never expires. That is a good decision and the audit should say so.
   - **DRIFTED**: degrades because nobody asked. No decision anywhere, just a dependency that arrived.
   - **BROKEN**: degrades in a way nobody would defend if asked - a hang, a silent failure, a lie on screen.
   The interesting finding is the second and third columns. The owner is not asking us to undo the Gateway; he is asking what we did without noticing.
4. **What you did NOT check**, named. An audit that does not say where it stopped looking is claiming a completeness it has not earned.
5. **No numbers you have not measured.** If you want to say something is big, measure it or say "unmeasured". The gap 4 paper had to withdraw a fabricated "15" that argued the opposite of its own conclusion - and its author found it only when asked which number he would refuse to believe. Ask yourself that question before you push.

## Out of scope

- The phone and the Cockpit. They are remote clients; needing the Gateway is what they ARE.
- Fixing anything.
- Re-opening gap 4 - the owner rejected it, and this audit is what his answer started.
