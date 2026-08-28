# Wilson: picking this up later

Written 28 August 2026, at the point where Wilson works and is being put down for a while.

The README says what Wilson is and how it works. This says what state it is in, what was decided and
why, and what the next person has to decide before they can go further. Read the README first.

## Where it stands

It works. It is deployed, it deploys itself on merge, and it does four things: answers questions,
keeps timers, tells you the weather, and shuts up when told to.

Everything below is either a decision that has already been made and should not be relitigated
without a reason, or a decision nobody has made yet.

## Decisions already made, and why

**The browser's own recogniser does the listening, not Whisper.** Whisper was measured and works —
`whisperListener.ts` is written and wired to nothing — but the platform recogniser needs no download,
no model, and starts instantly.

There is a trap here that cost most of a day. The recogniser **starts and immediately ends, with no
error and no results, when driven over the browser automation protocol.** It works perfectly in a
browser a person is using. That produced a confident and wrong conclusion that Web Speech was dead on
this machine, and nearly a rewrite onto Whisper. **A browser driven by automation is not evidence
about a browser a person is using.** If it ever genuinely fails on a real device, `whisperListener.ts`
is the replacement and the calibration screens already say which model that device should run.

**The model translates; the device executes and speaks.** The model is given tools and returns tool
calls only. The page runs them and composes the sentence from what actually happened. This is not
style: it is why Wilson cannot tell you a timer is set when it is not. Any new skill should follow
it. If a future skill needs the model to phrase something, that is a real departure and worth arguing
about first.

**No local shortcuts for commands with arguments.** A regular-expression fast path for timers was
built, shipped, and deleted the same day because it silently discarded names. Four hundred
milliseconds is not worth a wrong answer.

**Timers live in the page.** Deliberately. A browser cannot reliably wake itself, so a background
timer would be a lie. If timers ever need to survive the app being closed, that is a native
application or a push notification from the Gateway, not a cleverer web trick.

## Decisions nobody has made yet

**Search needs a key, and creating it is a human job.** There are no search keys on this machine.
Brave Search has a free tier; Tavily and Serper are the alternatives. Every one needs a signup.
Until somebody creates one, the options are: no search, or a keyless version built on Wikipedia and
DuckDuckGo instant answers, which handles "who was Ada Lovelace" but is not real web search.

**Knowing who is speaking is three different products and nobody has said which.** They need
different designs and have very different failure costs:

- *Do not answer the television.* Forgiving. A wrong answer is a missed command.
- *Know one person from another, and answer differently.* Harder. Needs enrolment per person.
- *Only obey one person.* A security feature, and it will lock you out when you have a cold.

Technically all three need a speaker-embedding model in the browser, an enrolment step, and raw audio
— which the platform recogniser does not give us, so `pcmCapture.ts` would have to run alongside it.
That much is known. **What is not known is which of the three was wanted**, and the answer changes
the design enough that building before asking would be waste.

Note also that none of it can be verified by one person: proving it distinguishes anybody needs at
least two voices.

**The directory is still called `cc-assistant` while the product is called Wilson.** Renaming is
cheap but it moves the Vercel root directory and the eventual Gateway mount, so do it deliberately.

**Wilson is not connected to DevThrottle at all.** No gateway, no device key, no fleet. That was
right for getting it working and is the open question for what it becomes: a DevThrottle feature
behind a sign-in, or its own thing. The decision recorded during the build was "a tool in
DevThrottle, because I cannot start another business", but nothing in the code commits to it yet.

## Things that will bite

**The service worker will serve you stale code.** It is network-first now, which fixes it, but if a
deploy ever seems not to have landed, add `?fresh=1` before concluding anything.

**Two watchers reported CI green when it was still running.** One tested a jq expression that returns
empty on error, so an error looked identical to success; the other detached, so the tool saw the
outer shell exit. If you write a watcher, make its pass condition a **presence** — the run reaching a
terminal state — never an absence.

**The Vercel deployment is connected to GitHub now.** It builds only when this directory changes.
That means a fix can no longer be hand-deployed; it has to be committed and merged. Slower, and
correct.

## If you want to keep going

In the order I would take them:

1. **Search**, keyless first. It is the biggest gap in usefulness and needs no decision from anybody.
2. **Warm the function**, so the first question of the day is not nine seconds.
3. **Endpointing.** Turn-taking is still whatever the browser does. The design for adaptive
   endpointing and an acknowledgement sound was worked out and never built; it is the difference
   between talking to it and operating it.
4. **Voice identity**, once somebody says which of the three it is.
