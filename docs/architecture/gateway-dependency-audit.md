# What a Director loses when it cannot reach its Gateway

**Status:** AUDIT ONLY. Nothing here is built, fixed, or changed. This is a findings document.
**Written:** 15 July 2026, by the Gateway Audit Manager.
**Brief:** [`gateway-dependency-audit-brief.md`](gateway-dependency-audit-brief.md). **Model:** [`gap-4-desktop-asks-recommendation.md`](gap-4-desktop-asks-recommendation.md).
**Everything below was read from `origin/main`** in a worktree verified as zero commits behind it.

---

## The one-line answer

**Yes, you can work on that laptop - the desktop itself is almost entirely local and barely notices.
You lose voice and you lose snooze, and both of those were deliberate choices we can defend. But
your AGENTS lose the ability to talk to each other on your own laptop, and that one nobody chose.**

What it feels like: you open the laptop on the train, your sessions are there, they run, you type at
them, the rail shows the right colours, you can create new sessions and close them. Press Speak and
it fails after a wait. Press Snooze and it tells you plainly that snooze needs a Gateway. Those are
the two you would expect.

Then an agent on that laptop runs `cc-devthrottle message send <other-session> "..."` to reach
another session sitting six inches away on the same machine, and it fails with "Cannot reach the
Gateway". Not because the message needs the Gateway - it does not, and the code that delivers it
handles a local target without one - but because the **lookup that finds the session** is routed
through the Gateway and refuses to answer without it.

**The distinction that runs through this entire document:** "no Gateway configured" and "Gateway
configured but unreachable" are different states. Your laptop is the second one. Nearly everything
that degrades gracefully degrades gracefully in the FIRST state. The second state is where the
defects live, and it is the only one you are ever actually in.

---

## The capability table

"Which state breaks it" is the important column. `unconfigured` = no Gateway address set at all.
`unreachable` = an address is set and nothing answers - **your case**.

| Capability | Works on the train? | Which state breaks it | What decides it |
|---|---|---|---|
| Creating, running, closing sessions | **Yes** | neither | `SessionManager` has no Gateway call on the creation path |
| The session rail's colours | **Yes** | neither | folds in-process from the local `Session` |
| Typing at a session, Send, Queue | **Yes** | neither | in-process, no Gateway seam |
| Session numbers | **Yes** (falls back) | neither | `SessionManager.cs:180-206` - offline number on any failure |
| Staying signed in | **Yes** | neither | `DevThrottleAccountService.IsLoggedIn()` - no network call |
| Explain / Wingman brief | **Yes** (local explain) | neither | `GatewayClient.cs:154-158` - catches, returns null |
| Cockpit button | No, but says so clearly | both (correctly) | `MainWindow.axaml.cs:2476-2510` - modal naming the URL |
| **Snooze** | **No - by design, says so** | both (correctly) | `MainWindow.axaml.cs:2189` - requires `Connected` |
| **Voice / transcription** | **No - by law** | both, *differently* | `GatewayTranscriptionClient.cs:40` vs `:55` |
| **Fleet list (`session list`)** | **No** | **unreachable only** | `ControlEndpoints.cs:321-344` |
| **Messaging a LOCAL session** | **No** | **unreachable only** | `session_ops.py:96-97` via the above |

---

## CHOSEN - degrades because someone decided it should

These are good decisions. The audit's job here is to say so, not to reopen them.

### Snooze - and it is the best-behaved code in this audit

`MainWindow.axaml.cs:2189` refuses to snooze unless
`GatewayMonitor.Status == GatewayConnectionStatus.Connected`. The comment says why: snooze needs a
round trip (Director to Gateway to record the timer, Gateway back down to set the hold), so it
requires a **verified** connection, which proves both legs.

This is the exemplar, and it matters far beyond snooze: **it checks reachability, not
configuration.** `GatewayConnectionStatus` is a real three-state monitor - `NotConfigured`,
`Connecting`, `Connected` - and its own comment says *"Green is EARNED: only a LIVE tunnel sets
Connected"* (`GatewayConnectionMonitor.cs:47`). The instrument for telling your two states apart
**already exists, is first-class, and is correct.** Remember that when you read the DRIFTED section.

You get "You need to be connected to a Gateway to use snooze." That is honest and correct.

### Voice and transcription - one path, by law

`GatewayTranscriptionClient.cs:40` throws a clear typed error when no Gateway is configured. This is
the one-transcription-path law, deliberately taken. Voice is gone on the train. That is the cost of
the law and the law is worth it. *(The unreachable half of this is not so clean - see DRIFTED.)*

### Refusing to use a local key when a Gateway is configured

`HostedAiKeyResolver.cs:172-178` catches an unreachable Gateway and returns null rather than
silently falling back to a local key. The comment names your exact state: *"Gateway configured but
unreachable: dictation is unavailable for now. We do not silently use a local key here - on a
Gateway, the Gateway is the source of truth."* Someone thought about the train and wrote it down.

### Session numbers - fall back cleanly, in both states

`SessionManager.cs:186-206`. The Gateway is asked for a number off the creation path
(`Task.Run`), so **session creation never blocks on the network**, and any failure - disabled,
unreachable, or pool exhausted - assigns a local offline number. The Architect's lead was correct.
On the train a new session is briefly numberless, then gets an offline number. Minor and honest.

### The Cockpit button - a model of good failure

`MainWindow.axaml.cs:2476-2510`. Eight-second timeout, then a modal naming the URL it actually
probed, with different wording for "no tailnet URL" versus "cannot reach it". Its comment:
*"a toolbar button that silently does nothing is just confusing."* Correct.

### Staying signed in

`DevThrottleAccountService.IsLoggedIn()` makes no network call and reads the cached credential. An
expired-but-well-formed token still reads as signed in while a background refresh retries, and an
offline refresh reports `Unavailable` (`BackendUnavailableTokenRefresher.cs:26`), which is
explicitly *not* a rejection and leaves the credential in place. Only a definitive backend rejection
clears it - which cannot happen with no backend to reject you.

**Answering the Architect's question directly: a laptop needs nothing to authenticate offline, and
it does not get locked out.** Login moving to the Gateway did not strand the train case.

---

## DRIFTED - degrades because nobody asked

### 1. The fleet directory refuses to answer a question it can answer locally

`ControlEndpoints.cs:321-344` is `GET /fleet/sessions`. Read the shape:

```csharp
var gw = gatewayClientProvider?.Invoke();
if (gw is { IsEnabled: true })
{
    try { return Results.Json(await gw.ListFleetSessionsAsync(ct)); }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"Cannot reach the Gateway: {ex.Message}" },
            statusCode: StatusCodes.Status502BadGateway);
    }
}
var local = sessionManager.ListSessions()...;   // line 339 - the local answer
return Results.Json(local);
```

`GatewayConfig.IsEnabled` is, in full (`GatewayConfig.cs:121`):

```csharp
public bool IsEnabled => !string.IsNullOrWhiteSpace(Url);
```

**It is a string check.** It asks "did somebody type an address into a file?" and the code above
reads the answer as "the Gateway will answer." Your laptop has an address typed in. So the local
answer on line 339 - the Director's own sessions, held in memory, needing no network at all -
**becomes unreachable code the moment a Gateway is configured, forever.**

The comment directly above it (line 319-320) says:

> *"With a Gateway, relay its aggregated list; standalone, serve this Director's own sessions (the
> no-Gateway acceptance criterion)."*

Someone was given an acceptance criterion - *this must work with no Gateway* - and implemented it
for the state where no address is configured. That criterion is met in the test suite and not in
your life.

**Why this is DRIFT and not a considered choice:** `GatewayConnectionStatus.Connected` exists, is
correct, and is used ten files away by snooze to answer exactly this question. Nobody decided
`/fleet/sessions` should use the weaker signal. It just did, and nothing pushed back.

**The honest counter-argument, stated fairly:** there IS a real case for the 502. If you ask for
"the fleet" and we can only see one machine, quietly handing you one machine's sessions is a lie -
it looks like the fleet shrank. `GatewayClient.cs:205-206` says as much: *"the /fleet/\* endpoints
are the boundary that turns a failure into a clear error, per the no-fallback rule."* That reasoning
is sound **for a genuine fleet query**. The third option nobody weighed is answering locally with an
explicit "the Gateway is unreachable, this is this machine only" marker - which is neither a
fallback nor a lie. I am not recommending it; I am recording that the choice was never framed.

### 2. The green suite certifies the state you are never in

`FleetMessagingTests.cs:100-105` sets `CC_DIRECTOR_ROOT` to a fresh temporary directory
*"so NO Gateway is configured for this Director"*, and line 204 then asserts
*"Standalone (no Gateway): the route serves this Director's own live sessions."*

That test passes. It will always pass. **It exercises the unconfigured state exclusively.** The
unreachable state - a configured address that does not answer, which is the only state your laptop
is ever in - has no test anywhere I looked. This is the repository's own recurring failure mode: a
green suite certifying a capability in the one condition the user is never in.

### 3. Transcription's two states are handled with different care

`GatewayTranscriptionClient.cs:40` throws a clean, typed, human-worded
`TranscriptionUnavailableException` when unconfigured. But when a Gateway **is** configured and does
not answer, `_http.SendAsync` at line 55 is not wrapped, so a raw network exception escapes. It
reaches `SpeakDialog.axaml.cs:596`, which does `SwitchToFailed(ex.Message)` - putting a raw .NET
network message on screen where the unconfigured path would have shown a sentence written for a
human.

Nobody chose to word one state carefully and let the other fall through. Same pattern as the rest of
this section: the state we tested got the care.

---

## BROKEN - degrades in a way nobody would defend

### Your agents cannot talk to each other on your own laptop

This is the finding. It is a direct consequence of the drift above, and unlike the fleet-list
question there is no argument on the other side.

**The chain, proven end to end rather than inferred:**

1. `cc-devthrottle message send <target> "text"` calls `_resolve_target`
   (`tools/cc-devthrottle/src/session_ops.py:422`).
2. `_resolve_target` (`:96-97`) calls `_get_sessions()` **first**, to turn your target into a
   session id.
3. `_get_sessions` (`:87-93`) calls `director.get_json("fleet/sessions")` and, on any
   `DirectorError`, prints the error and exits 1.
4. That request goes to `CC_DIRECTOR_API`, which is **loopback** - your own Director on your own
   machine (`tools/cc_shared/director.py:31-38`).
5. Your own Director, on loopback, returns **502 "Cannot reach the Gateway"** - because of
   `IsEnabled` above.

**Why it is indefensible:** the delivery endpoint sitting right behind that lookup is
`POST /fleet/send`, and its own comment (`ControlEndpoints.cs:346-348`) says:

> *"A local target is delivered directly (works with or without a Gateway); a remote target is
> relayed through the Gateway."*

**The capability is present, correct, and deliberately built to work without a Gateway. It is
unreachable because the lookup in front of it will not answer.** Someone wrote "works with or
without a Gateway" one screen below the code that guarantees you can never get there.

**What it costs you, concretely.** Every command that names a target dies the same way, because they
all resolve first: `message send`, `message ask`, `session rename`, `hold`, `interrupt`, `buffer`,
`role`, `done` (`session_ops.py:210-353, 422, 446`). Also `session list` and `session whoami`
(`:137, :183`). **Even passing a full, exact session id does not help** - `_resolve_target` runs the
lookup regardless of how precisely you named the target.

What still works: commands that target **yourself** with no argument. `resolve_target_or_current`
(`:120-129`) short-circuits to `CC_SESSION_ID` without touching the directory. So a session can
still rename itself or mark itself done. It simply cannot address a neighbour.

**Why this is the big one for you specifically.** On that laptop you are not running one agent - you
run a fleet, and the fleet's whole coordination model is agents messaging each other. A Manager
cannot ping its Architect. An Architect cannot reach its Manager. Missions on a disconnected laptop
cannot coordinate at all, on a machine where every one of those sessions is local and needs no
network whatsoever.

### A second, smaller one: the wait before the failure

Two paths have no reachability precheck and rely on a network timeout to discover what
`GatewayConnectionStatus` already knows:

- Transcription's client has `Timeout = TimeSpan.FromMinutes(5)`
  (`GatewayTranscriptionClient.cs:17`).
- `GatewayClient`'s has `Timeout = TimeSpan.FromSeconds(10)` (`GatewayClient.cs:123`).

**These are constants I read, not durations I measured, and the difference matters.** How long you
actually wait depends on *how* the network fails: a refused connection fails almost immediately, a
black-holed route runs to the ceiling. I did not measure either. I am not claiming you wait five
minutes for a failed dictation - **I am claiming nothing stops you from waiting five minutes, and
nobody has checked.**

How to find out: put a laptop with a configured, unreachable Gateway on a foreign network, press
Speak, and time it. That is a ten-minute experiment and it would replace this paragraph with a fact.

---

## What I did NOT check

Named plainly, so nobody spends their scepticism in the wrong place.

- **I never ran the product offline. Not once.** Every finding here is read from source. The chains
  are traced call-by-call and I believe them, but "I read the code" is not "I watched it happen."
  The `/fleet/sessions` 502 in particular deserves ten minutes of somebody disconnecting a laptop
  and running `cc-devthrottle session list`. **I did not do it, so treat the CLI finding as
  proven-by-reading, not proven-by-running.**
- **I measured no timings.** See above. Every duration in this document is a constant read from a
  source file, labelled as such.
- **I did not check the Learn button** (`MainWindow.axaml.cs:2543`). It logs the same
  "opening nothing" line as the Cockpit button and looks like the same pattern, but I read Cockpit
  and inferred Learn, and inferring is what this document is supposed to catch. Undiagnosed.
- **I did not trace where a failed BACKGROUND dictation surfaces, and it is the most promising
  undiagnosed lead in this document.** The transcription failure I describe above is the path
  through `FinalizeFromRecordingAsync` (`SpeakDialog.axaml.cs:581-597`), which I read: broad catch,
  raw message on screen. But `SpeakDialog.axaml.cs:572-573` is a **different** path - Send "hand[s]
  the recorder to background transcribe-and-submit, closing now". The dialog is gone before the
  Gateway is ever called. **I do not know what you see when that background transcription fails on
  a train, and "a silent failure" is exactly the category this audit is meant to catch.** How to
  find out: read the background submit path from that line and follow its catch. I ran out of
  audit before I did, and I am not going to guess.
- **I did not audit the Gateway's own behaviour**, only the Director's view of it.
- **I did not check the launcher tray** (`LauncherTrayController.cs:158-159`), whose "Open Cockpit"
  and "Settings" items build URLs from the Gateway address. They plausibly open dead links on the
  train. Undiagnosed.
- **I did not check scheduled runs, email, or the mission/spawn paths** in the CLI
  (`schedule_ops.py`, `email_ops.py`, `mission_ops.py`). `spawn` is noted at `session_ops.py:548`
  as spawning locally with no `--machine`, which suggests it survives, but I did not trace it.
  Undiagnosed.
- **I did not enumerate non-button affordances** - selection changes, drag-and-drop, terminal mouse
  interaction. The surface walk covered all 79 interface files for buttons, menus, and shortcuts.
- **I did not investigate whether the desktop rail would survive gap 4's Option A**, which asks the
  exact question this audit answers from the other side. Somebody should read these two together.

## The numbers in this document

There are four: 10 seconds, 5 minutes, 8 seconds, and 79 interface files. **Every one is a constant
read from a named file or a count of files walked. None is a measurement of anything happening.**

The paper this one is modelled on had to withdraw a fabricated "15" that argued against its own
conclusion, and its author only caught it when asked which number he would refuse to believe. Mine
is **5 minutes**: it is the most alarming figure here, it is the one a reader will quote, and it is
the one I am least able to stand behind - because a five-minute ceiling is not a five-minute wait,
and I never watched the clock. If it reappears anywhere as "dictation hangs for five minutes",
that is my sentence being misread, and the fix is to go and time it.

## Answering the Architect's eight leads

Verified against the code. Seven stand; one needs a correction and one needs a sharpening.

| # | Lead | Verdict |
|---|---|---|
| 1 | Gateway hands out numbers (#1292), do not revert to local | **Correct.** The local fallback is not a bug; it is the designed offline path and never blocks creation. |
| 2 | Login removed to the Gateway (#664) - what does a laptop need? | **Correct, and the answer is: nothing.** Cached credential, no network call, no lock-out. |
| 3 | Snooze became Gateway-owned (#1375-#1388) | **Correct** - and it is the best-behaved code in the audit. |
| 4 | One transcription path is a law | **Correct** for the unconfigured state. **Sharpening:** the unreachable state leaks a raw network error the unconfigured state words carefully. |
| 5 | Overlay colours moved to the Gateway; rail is dumb | **Correct, and it does not hurt you here.** The rail folds locally and shows colours on the train. |
| 6 | The tunnel-only cut - what still talks to the Director on a disconnected laptop? | **You were right to look hardest here, and the answer is worse than you feared.** The CLI reaches the Director on loopback and that part survives the cut fine. What does not survive is that the Director then refuses to answer from loopback. It is not the tunnel cut that broke it - it is `IsEnabled`. |
| 7 | Hosted AI gated on credits and key (#937) | **Correct.** Voice needs the Gateway and an account; the key resolver refuses a local key by design. |
| 8 | "Voice unavailable = offline Director" (#1194) is a named degraded state - a model? | **Partly.** The better model is snooze: it is the one place that checks `Connected` rather than `IsEnabled`, and it is the pattern the two defects above are missing. |

---

## What the owner should take from this

You asked what the sum of a hundred good decisions costs a man on a train. The answer is not what I
expected when I started.

**The Gateway decisions themselves are fine.** Snooze, voice, numbers, login, the colours - each one
degrades about as well as it can, several of them have comments explicitly naming your exact
situation, and one of them (snooze) is a model the rest of the codebase should copy. If the audit
had found only these, the answer to your question would be "we drifted, and it cost you voice and
snooze, and you knew that."

**The cost is not in the decisions. It is in a word.** `IsEnabled` means "somebody typed an
address." Read as "the Gateway will answer," it is wrong exactly when you are on a train and never
when you are at your desk - so it is invisible in every test, at every desk, in every review, and it
is only ever wrong for you, in the one situation you asked about.

**And the thing it takes from you is not a feature. It is your fleet's ability to talk to itself on
one disconnected machine** - where nothing it needs is more than six inches away.

**Recommended, and it is genuinely your call:** treat the fleet-list question and the local-messaging
cascade as **two separate decisions**, not one bug. The cascade has no counter-argument and no
defender. The 502 does have an argument, and the interesting third option - answer locally, but say
loudly that it is local-only - was never put on the table, so putting it there is the decision worth
your attention rather than mine.

**And the cheap test that should exist and does not:** one that configures a Gateway address,
points it at nothing, and asserts what a Director does. Every defect in this document would have
been caught by it, and the reason none of them were is that the suite only ever asks what happens
when nobody typed an address.
