# What a Director loses when it cannot reach its Gateway

**Status:** AUDIT ONLY. Nothing here is built, fixed, or changed. This is a findings document.
**Written:** 15 July 2026, by the Gateway Audit Manager.
**Brief:** [`gateway-dependency-audit-brief.md`](gateway-dependency-audit-brief.md). **Model:** [`gap-4-desktop-asks-recommendation.md`](gap-4-desktop-asks-recommendation.md).
**Everything below was read from `origin/main`** in a worktree verified as zero commits behind it.

---

## What is OBSERVED and what is only READ - before anything else

This audit is mostly a code reading. **One thing in it was actually run, and it is the headline.**
Everything else is a prediction from cited code. The difference is marked at every claim below with
these two tags, and nothing carries a tag it has not earned:

- **[OBSERVED]** - I staged it and watched it happen. The transcript is in
  [The experiment](#the-experiment-observed) below.
- **[CODE-READ]** - traced call-by-call through cited code, **not run**. Believable, not proven.

**[OBSERVED]:** the fleet directory returning 502 instead of the local list, and the entire
cc-devthrottle command-line tool failing on a disconnected laptop - including messaging a local
session. That was staged with a real Director and the real installed tool, against a control.

**[CODE-READ]:** everything about voice, snooze, the buttons, the account, timings, and how any of
it *feels*. **I have never run this product offline on a real laptop.** Where I write "you open the
laptop on the train", that is a prediction from source, not a report from a train.

---

## The one-line answer

**Yes, you can work on that laptop - the desktop itself is almost entirely local and barely notices.
You lose voice and you lose snooze, and both of those were deliberate choices we can defend. But
your AGENTS lose the ability to talk to each other on your own laptop, and that one nobody chose.**

**[CODE-READ]** What it should feel like, predicted from source and not watched: you open the laptop
on the train, your sessions are there, they run, you type at them, the rail shows the right colours,
you can create new sessions and close them. Press Speak and it fails after a wait. Press Snooze and
it tells you plainly that snooze needs a Gateway. Those are the two you would expect. *Nobody has
put a laptop on a train and checked any of this sentence.*

**[OBSERVED]** Then an agent on that laptop runs `cc-devthrottle message send <other-session> "..."`
to reach another session sitting six inches away on the same machine, and it fails with **"Cannot
reach the Gateway"**. Not because the message needs the Gateway - it does not, and the code that
delivers it handles a local target without one - but because the **lookup that finds the session**
is routed through the Gateway and refuses to answer without it. **I staged this and watched it.**

**[OBSERVED]** And it is worse than one route. `cc-devthrottle message send all` - the command the
Director's own briefing tells every agent to use to reach its team - dies the same way through
**completely different code** that never touches that lookup. Two independent paths, one defect.

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
| Creating a PLAIN session, running, closing | **Yes** [CODE-READ] | neither | `SessionManager` has no Gateway call on the creation path |
| Creating a session **attached to a mission** | **No** [CODE-READ] | **unreachable** | `ControlEndpoints.cs:564-582` - 502s *before* it creates |
| The session rail's colours | **Yes** [CODE-READ] | neither | folds in-process from the local `Session` |
| Typing at a session, Send, Queue | **Yes** [CODE-READ] | neither | in-process, no Gateway seam |
| Session numbers | **Yes** (falls back) [CODE-READ] | neither | `SessionManager.cs:180-206` - offline number on any failure |
| Staying signed in | **Yes** [CODE-READ] | neither | `DevThrottleAccountService.IsLoggedIn()` - no network call |
| Explain / Wingman brief | **Yes** (local explain) [CODE-READ] | neither | `GatewayClient.cs:154-158` - catches, returns null |
| Cockpit button | No, says so clearly [CODE-READ] | both (correctly) | `MainWindow.axaml.cs:2476-2510` - modal naming the URL |
| Learn button | No, says so clearly [CODE-READ] | both (correctly) | `MainWindow.axaml.cs:2543-2578` - same pattern as Cockpit |
| **Snooze** | **No - by design, says so** [CODE-READ] | both (correctly) | `MainWindow.axaml.cs:2189` - requires `Connected` |
| **Voice / transcription** | **No - by law** [CODE-READ] | both, *differently* | `GatewayTranscriptionClient.cs:40` vs `:55` |
| Schedules, missions, owner email (command-line) | **No - Gateway-hosted** [CODE-READ] | both (by nature) | direct Gateway clients; handled `GatewayError` |
| **Fleet list (`session list`)** | **No** **[OBSERVED]** | **unreachable only** | `ControlEndpoints.cs:321-344` |
| **Messaging a LOCAL session** | **No** **[OBSERVED]** | **unreachable only** | `session_ops.py:96-97` via the above |
| **`message send all` (your team)** | **No** **[OBSERVED]** | **unreachable only** | `ControlEndpoints.cs:954-965` - a *separate* route |

---

## The experiment [OBSERVED]

The Architect ordered the headline proved or disproved by running it, because a behaviour claim
resting on a code reading is the weakest strong sentence in a document. **It reproduces.**

**How it was staged, safely.** No Director of yours was touched, and no real Gateway was contacted.
`GatewayConfig.Load()` reads `config.json` under `CC_DIRECTOR_ROOT`, so a throwaway harness pointed
that at a fresh temporary directory and started a **real `ControlApiHost`** on an ephemeral loopback
port - the same class that serves your Director. Its `config.json` named a Gateway at
`http://127.0.0.1:47113`, a port verified as having nothing listening. Then the **real installed
`cc-devthrottle`** was driven at it. The harness was deleted afterwards; it is not in this branch.

**Why a refused port is the strongest form of the test, not a weaker one:** a refused connection
means the Director learns *instantly and definitively* that no Gateway is there. It has the best
information it will ever have. It still refuses to answer.

### Leg 1 - the Director's own fleet directory

| State | `IsEnabled` | `GET /fleet/sessions` | Elapsed |
|---|---|---|---|
| **Control** - no `gateway.url` at all | `False` | **200 OK**, body `[]` | 18 ms |
| **Treatment** - `gateway.url` set, nothing listening | `True` | **502 BadGateway** | 2051 ms |

```
{"error":"Cannot reach the Gateway: No connection could be made because the
 target machine actively refused it. (127.0.0.1:47113)"}
```

### Leg 2 - the real command-line tool, same machine, same commands

| Command | **Control** (no Gateway) | **Treatment** (configured, dead) |
|---|---|---|
| `session list` | `No sessions are running in the fleet.` | `Error: Cannot reach the Gateway: ...actively refused it.` |
| `message send abc123 "hello"` | **`No session matches 'abc123'.`** | `Error: Cannot reach the Gateway: ...actively refused it.` |

**That control row is the whole proof.** In the control the resolver *ran*, looked for `abc123`, and
correctly said it did not exist. In the treatment the same command on the same machine never got
that far - it died at the directory lookup and reported the Gateway instead. **Same tool, same
command, same target, same machine. The only difference is a URL typed into a file.**

### What the experiment did NOT settle

- It used a **refused** connection, not a black-holed one. It therefore says nothing about the
  timeout question below, and the 2051 ms is not a wait you would feel. **I did not diagnose why an
  instantly-refused connection takes two seconds** - I only measured that it does.
- It ran a Director with **no live sessions**. That is sufficient (the error arrives from the
  lookup, before target matching, which is exactly what the control demonstrates) but it is not the
  same as watching a real Manager fail to reach a real Architect.
- It proves nothing about voice, snooze, or any desktop button. Those remain **[CODE-READ]**.

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

### The Cockpit and Learn buttons - a model of good failure

`MainWindow.axaml.cs:2476-2510`. Eight-second timeout, then a modal naming the URL it actually
probed, with different wording for "no tailnet URL" versus "cannot reach it". Its comment:
*"a toolbar button that silently does nothing is just confusing."* Correct.

**Learn is the same** (`:2543-2578`) - I listed it as unchecked in the first draft while citing its
handler, which is not a caveat, it is a gap I could have closed in the time it took to write the
sentence. Read now: identical eight-second probe, identical modal, *"never a silent no-op and never
a loopback URL that only works on this machine"*. Both buttons behave well on the train.

### A mission-attached spawn fails loud - and its comment names this audit's whole subject

Creating a plain session never touches the Gateway. But a **local** spawn carrying a mission id and
no mission name resolves that name against the Gateway's mission store first
(`ControlEndpoints.cs:564-582`), and on the train returns 502 **before creating anything**:

> *"Fail loud. An unreachable Gateway is NOT an unknown mission, and reporting it as one is the
> exact lie this issue is about."* - `ControlEndpoints.cs:576-577`

Somebody hit this audit's exact distinction, in this exact file, and got it exactly right. It is a
deliberate, correct choice: mission names live in the Gateway, and inventing one locally would be
the lie the comment refuses to tell. **It does mean my first draft's "creating sessions works
offline" was overbroad** - plain creation works, mission-attached creation does not - and the table
above now says so.

### Schedules, missions, and owner email - Gateway-hosted by nature

The command-line tool's `schedule`, `mission`, and `email` verbs are **direct Gateway clients**: they
call the Gateway's own Control API rather than going through a Director, because the data lives
there (schedules, mission records) or the relay is server-side (email). Each carries its own handled
`GatewayError` - *"A handled, user-facing failure talking to the Gateway"* - so on the train they
fail with a worded error rather than a crash.

These are CHOSEN and belong in the same bucket as the phone and the Cockpit: needing the Gateway is
what they *are*. **I flagged them as unchecked at the bottom of my first draft while printing a
complete-feeling answer at the top. A caveat two hundred lines below the headline is not a caveat**,
and they are classified here instead.

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

### The same defect by a different road - and this one is worse

**My first draft got the conclusion right and the mechanism incomplete, and the Architect caught
it.** I found the targeted path (`_resolve_target`) and reported it as *the* cascade. It is not.
**`message send all` never touches `_resolve_target` at all** - `send_message` posts straight to
`fleet/broadcast` (`session_ops.py:409-417`). It dies anyway, in its own code:

```csharp
// ControlEndpoints.cs:954
if (gw is { IsEnabled: true })
{
    try { fleet = await gw.ListFleetSessionsAsync(ct); }
    catch (Exception ex)
    {
        return Results.Json(new FleetSendResponse { Accepted = false,
            Error = $"Cannot reach the Gateway: {ex.Message}" },
            statusCode: StatusCodes.Status502BadGateway);   // line 963
    }
}
else            // line 967 - the local team list, unreachable whenever a URL is configured
```

Same `IsEnabled`. Same 502. **Entirely different route.** And the comment three lines above it
(`:938-939`) makes the same promise the fleet-list route made: *"Standalone (no Gateway) it delivers
to the in-team sessions this Director can see."*

**This matters more than the defect I led with**, and the Architect is right about why: `message
send all` is what the Director's own fleet briefing explicitly tells **every agent** to use to reach
its team. It is not a corner. It is the documented default for the most common thing an agent does.

**The correction worth recording:** two independent code paths, written at different times, both
reach for `IsEnabled`, both promise a local answer in a comment, and both withdraw it the moment an
address is typed. That is what makes this DRIFT rather than a bug - **one bug is a mistake; the same
wrong question in two unrelated routes is a habit.** A reader who fixes only the path I found would
leave the more important one standing, which is exactly what would have happened had this document
shipped as I first wrote it.

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

- **I never ran the DESKTOP offline, and I never put a laptop on a train.** The command-line finding
  is now **[OBSERVED]** against a staged Director (see [The experiment](#the-experiment-observed)),
  but **every claim about voice, snooze, the buttons, the rail and the account remains
  [CODE-READ]** - traced through cited source, never watched. The whole "what it feels like"
  paragraph is a prediction.
- **The experiment used a REFUSED port, not a real disconnected network.** A laptop whose Gateway
  sits behind a dead tailnet route may fail differently and much more slowly. What I proved is that
  the refusal path 502s; I did not prove what a train's network does.
- **I measured no timings other than the experiment's two.** Every other duration here is a constant
  read from a source file, labelled as such.
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
- **I did not enumerate non-button affordances** - selection changes, drag-and-drop, terminal mouse
  interaction. The surface walk covered the desktop's declarative buttons, menus, and keyboard
  shortcuts. **It did not cover a reproducible count of files** - see the withdrawn "79" below.
- **The surface walk was done by a subagent and I verified only the parts I cite.** Where this
  document names a file and line for a button, I opened it. Where it says "the rest are local", that
  rests on that inventory, and one number in it turned out to be unreproducible - which is a reason
  to treat the unverified remainder as a lead, not a finding.
- **I did not investigate whether the desktop rail would survive gap 4's Option A**, which asks the
  exact question this audit answers from the other side. Somebody should read these two together.

## This paper's own defect, on the record - the withdrawn "79"

**The first draft of this document said I walked "all 79 interface files". That number is
withdrawn. It is not reproducible, I did not measure it, and I did not write it.**

A subagent's inventory reported 79; I passed it into the document in my own voice, as my own count,
and then - in the very section where I named my weakest number - **certified it as "a count of files
walked".** It was not. It was a number I had accepted on trust and relabelled as evidence.

Counted properly on `origin/main`, no rule produces 79:

```
git ls-tree -r --name-only origin/main -- src/CcDirector.Avalonia | grep -c '\.axaml$'   # 74
git ls-tree -r --name-only origin/main -- src                     | grep -c '\.axaml$'   # 78
```

74 in the desktop project; 78 across every project. The Architect counted 74 independently; an
inspector got 74, 152, or 197 depending on the rule. **Nobody lands on 79.** The honest statement is
the one that needed no number: *I walked the desktop surfaces named in this document, and the
inventory's stopping points are listed below.*

**Why this is worth a section rather than a quiet correction.** This is the exact defect the gap 4
paper had to withdraw - a fabricated *measurement*, sitting among facts, reading like a fact. I
wrote a section called "the numbers in this document", declared no number was a measurement, named
the five-minute figure as the one I least stood behind, **and inside that same section certified the
one number that was actually false.** The five-minute figure I had hedged was fine, because I had
hedged it. The 79 was dangerous *because it looked like the boring one*.

The lesson is not "check your numbers". It is that **a number arriving from somewhere else, in a
report you did not produce, becomes yours the moment you type it in your own voice** - and the audit
of your own document does not scrutinise it, because by then it is already furniture. I asked myself
which number I would refuse to believe and answered "5 minutes". I should have asked which number I
had never personally produced.

## The numbers in this document

There are three: 10 seconds, 5 minutes, and 8 seconds. **Every one is a constant read from a named
source file. None is a measurement of anything happening.** (A fourth, "79", is withdrawn above.)

Separately, the experiment produced **two real measurements** - 18 ms and 2051 ms - and they are
labelled **[OBSERVED]** because I watched a stopwatch print them. They are the only measured
durations in this document.

The figure I least stand behind remains **5 minutes**: it is the most alarming, the one a reader
will quote, and a five-minute *ceiling* is not a five-minute *wait*. If it reappears anywhere as
"dictation hangs for five minutes", that is my sentence being misread, and the fix is to go and
time it.

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

**And it is not one mistake, it is a habit.** Two unrelated routes - the fleet directory and the
team broadcast - written at different times, both reached for the same wrong question, and both
wrote a comment promising a local answer they then made unreachable. A third and fourth place
(snooze, the mission spawn) asked the *right* question and got it right, and one of those has a
comment naming this audit's exact distinction. **The knowledge is in the building. It just is not in
those two routes.**

**And the thing it takes from you is not a feature. It is your fleet's ability to talk to itself on
one disconnected machine** - where nothing it needs is more than six inches away. That part is no
longer a theory: it was staged and watched, and the control proves the only variable is a typed URL.

**Recommended, and it is genuinely your call:** treat the fleet-list question and the messaging
cascade as **two separate decisions**, not one bug. The cascade - both routes - has no
counter-argument and no defender: `message send all` is what your own briefing tells every agent to
use. The 502 on a genuine fleet *query* does have an argument, and the interesting third option -
answer locally, but say loudly that it is local-only - was never put on the table, so putting it
there is the decision worth your attention rather than mine.

**And the cheap test that should exist and does not:** one that configures a Gateway address,
points it at nothing, and asserts what a Director does. I wrote a throwaway version of exactly that
test in about twenty minutes, watched it catch the defect, and then deleted it, because a fix
smuggled into an audit is a fix nobody reviewed. **Every defect in this document would have been
caught by that test.** The reason none of them were is that the suite only ever asks what happens
when nobody typed an address - which is the one thing that is never true on your laptop.
