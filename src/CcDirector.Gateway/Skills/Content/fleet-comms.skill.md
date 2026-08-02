# Fleet communication between sessions

DevThrottle lets a session talk to other sessions running anywhere in the fleet, meaning any
machine whose Director is attached to the same Gateway. Use the single `cc-devthrottle` command.
You never need the Gateway URL or any token; your own Director relays for you.

Every session is launched with two environment values the command relies on:
`CC_DIRECTOR_API` is your own Director's address, and `CC_SESSION_ID` is your own id.
`cc-devthrottle` reads them automatically.

## Discover actions

Use this first when mapping a user task to a command.

```
cc-devthrottle actions --json
```

## Sessions

```
cc-devthrottle session list
cc-devthrottle session whoami
cc-devthrottle session rename "Dev Throttle Review"
cc-devthrottle session rename 9b2f "Frontend Review"
cc-devthrottle session spawn D:\path\to\repo --purpose "implement #799"
cc-devthrottle session spawn D:\path\to\repo --name "Frontend review"
cc-devthrottle session spawn D:\path\to\repo --purpose "run the test suite" --agent ClaudeCode --prompt "Run the tests and report failures."
cc-devthrottle session spawn D:\path\to\repo --name "frontend" --agent RawCli --command cmd
cc-devthrottle director list
cc-devthrottle session spawn D:\path\to\repo --name "build" --director "North build"
```

`--machine <name>` starts the session on another COMPUTER; `--director <id-or-name>` starts it on ONE
named Director. They answer different questions: a computer runs several named Director instances, so
`--machine` lands on whichever is listed first. Name the Director when it has to be that one -
`director list` gives you the id, and a Director's toolbar Copy button hands out its name, id, and
machine for pasting. An unregistered or ambiguous name fails loudly and never falls back to another
Director.

Always name your session. On this fleet many sessions run in the SAME checkout, so a session with
no name displays as the bare folder name and is impossible to tell apart. Lead with `--name`
(an explicit display name) or `--purpose` (a short description of what the session is FOR, e.g.
`implement #799`); spawn warns when you give neither. A blank name, or a name equal to the bare
repository folder name, is rejected - pass something meaningful or a purpose.

### If rename, done, or "message send all" answers "HTTP 404 from the Director"

The command is right and the code is right. The Director you are talking to is too old.

These verbs travel to the Director's loopback floor - rename to `POST /fleet/rename`, done to
`POST /fleet/done`, send-all to `POST /fleet/broadcast`. All three were restored to the floor
AFTER the v1.1.0 release, so a Director built before that restoration does not have those
routes and answers 404 to a perfectly correct call.

**Do NOT use `/healthz` version to decide this.** The version comes from
`Directory.Build.props`, which still reads `1.1.0` on main - so the shipped v1.1.0 Director
that LACKS the routes and a fresh build from main that HAS them both report `"version":"1.1.0"`.
Verified 2026-07-14 by running both side by side. The version cannot tell them apart.

Probe the route itself, and compare it against a route you know is fake.

The Director's floor requires a credential on every route but `/healthz`, so a probe sent without one
is answered 401 and says nothing about whether the route exists. Present the credential your session
was given at launch:

```
curl -s -o /dev/null -w "%{http_code}\n" -X POST http://127.0.0.1:7879/fleet/rename \
  -H "Authorization: Bearer $CC_DIRECTOR_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"toSessionId":"00000000-0000-0000-0000-000000000000","name":"probe"}'
curl -s -o /dev/null -w "%{http_code}\n" -X POST http://127.0.0.1:7879/fleet/definitely-not-a-route \
  -H "Authorization: Bearer $CC_DIRECTOR_TOKEN" -d '{}'
```

**Read 401 and 403 as answers about your CREDENTIAL, never about the route.** A session credential is
deliberately refused on `/fleet/rename` and the rest of the dangerous set, so BOTH probes answering
403 means your credential cannot reach either - which is a different fact from the route being
missing, and reading it as "the route is gone" would send you to update a Director that is fine. Run
the comparison through `cc-devthrottle`, which carries full authority, when you need to tell those
apart.

- Both 404 -> the route genuinely is not there. The Director is older than the restoration;
  update it. There is no workaround.
- The real route answers something OTHER than 404 (a JSON body, 400, 502) while the fake one
  404s -> the route exists and your problem is elsewhere. A 502 with a JSON body just means it
  ran and the relay failed - that is the route working.

Do NOT reach for the Gateway as a substitute route; it answers 401 for this and is not the
agent-facing surface.

This is worth a beat of suspicion generally: a Director, a `cc-devthrottle`, and a checkout can
each be older than origin/main, and a stale one will contradict the code you just read. Verify
what is running before you conclude a feature is broken (issue #1514).

### Display-name convention (ratified by Soren, 2026-07-11)

Names are how the fleet sorts, so compose them so related sessions group together:

- A session on a Mission is named mission first, role second, joined with " - ":
  - `Gateway Connection - Architect`
  - `Gateway Connection - Manager`
  - `Gateway Connection - Worker - connect panel` (a Worker adds its task at the end)
- Sorting by name then puts every session of one Mission next to each other, Architect and
  Manager adjacent with the Workers under them.
- NEVER put the repository in the name - the session list already shows the repository in its
  own column. NEVER put session ids or numbers in the name.
- A solo session (no Mission) is named for the work itself ("Clean up stale branches"), again
  without the repository name.

`session rename "name"` renames the current session using `CC_SESSION_ID`.
`session rename <target> "name"` renames another session selected by id prefix or exact name.

### Phased missions: a fresh Manager each phase (the Architect's job)

When an implementation runs in phases, do NOT keep one Manager alive across all of them. At each
phase boundary the Architect **retires the current Manager and spawns a fresh one**, briefed on only
what is done and what this phase needs.

Why it is worth it: a long-lived Manager drags every earlier phase's context forward - it drifts,
answers worse (a fast model can even hallucinate that work is done), and burns tokens re-reading
stale history it no longer needs. The durable truth - decisions, what shipped, what is next - lives
in the mission document and memory, so a fresh Manager reads those and starts clean with nothing
important lost. Cheaper and sharper, every phase.

At each phase boundary:
1. Confirm the current Manager is stood down (its tree is clean, nothing in flight).
2. Reap it. To reap ANOTHER session (a Manager reaping a Worker, or you reaping the outgoing
   Manager), call the Gateway front door: `curl -X DELETE http://127.0.0.1:7878/sessions/<full-session-id>`
   (the Gateway routes it to whichever Director hosts the session over the tunnel), or have the user
   close its tab. A session reaps ITSELF with `cc-devthrottle session done`, which flags the current
   session (`CC_SESSION_ID`) for graceful removal without killing it mid-turn.
3. Spawn a fresh Manager with a tight brief: `session spawn <repo> --name "<Mission> - Manager"`,
   pointing it at the mission document, stating plainly what is DONE and only THIS phase's goal.

This only works because the mission document and memory hold the state - keep them current so a reset
never loses anything.

## Messages

```
cc-devthrottle message send 4c810000 "I finished the API layer - you can start the frontend."
cc-devthrottle message send docs "Please update the API page when you get a chance."
cc-devthrottle message send all "Heads up team: I am about to rebase our shared branch."
cc-devthrottle message ask 9b2f "What database schema is loaded in your repo?"
cc-devthrottle message ask docs "What is the title of the API page?" --timeout-ms 60000
```

Send to the specific sessions that need to hear from you - by id prefix or name. `message ask` is
always single-target and waits for the target's answer.

## Who you may message, and the broadcast rule

Every incoming fleet message interrupts the receiving agent: it is typed into that agent's composer
and starts a turn. So a message you send is a demand on someone else's attention. Keep it scoped.

- Default scope is your own team: the sessions in your Mission, or - if you are a solo session -
  the sessions in the same repository on the same machine. A manager and its workers are one team.
  Message those sessions freely.
- `message send all` reaches ONLY your team, not the whole fleet. This is the everyday broadcast:
  use it for a heads-up your teammates need. It never touches sessions in other repositories or
  other missions, so it will not freeze the fleet.
- For git coordination on a shared working tree, `message send all` already reaches only the
  sessions that share your checkout - that is exactly who a shared-tree hold concerns.
- A WHOLE-FLEET broadcast (`message send all --everyone`) is different: it interrupts every session
  on every machine and repository. The Gateway Hub refuses it unless a human has issued a broadcast
  grant, and it requires a `--reason`:
  `cc-devthrottle message send all "..." --everyone --reason "why" --grant <id>`.
  Almost nobody should need this. If you think you do, ask the human for a grant - do not try to
  route around the Hub (it enforces the limit and also rate-limits repeated broadcasts). See issue #1229.

## Health check

```
cc-devthrottle selftest
```

This spawns two throwaway local sessions, proves list/send/ask works, then tears them down.

## Related surfaces

The same binary also owns Gateway schedules and local setup diagnostics:

```
cc-devthrottle schedule list
cc-devthrottle setup status
```

## Rules

- Address a session by a short id prefix or by exact name.
- For a simple current-session rename, run `cc-devthrottle session rename "New Name"` directly.
- If a target is ambiguous, rerun with a longer id prefix.
- If a command says `CC_DIRECTOR_API` is missing, you are outside a DevThrottle-launched session.
- The command never exposes the Gateway token to the session.
