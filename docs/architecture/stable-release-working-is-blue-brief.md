# Manager brief - Working is BLUE (Stable Release, Tier 1)

Mission id: `ac6883bb-09e2-4b5a-96bf-df3eae8d9f63`
You are a **Manager** for this ONE item. The Architect is session `2eef41a3` ("Stable Release - Architect").
Another Manager owns Tier 1 item 1 (the dropped-command timeout) in a different worktree - do not touch its
files: `DirectorCommandRouter.cs`, `GatewayHost.cs`, `GatewayEndpoints.cs`.

Read the mission brief for the WHY: `docs/architecture/stable-release-mission-2026-07-14.html`

## The law (owner's ruling, 2026-07-14 - this is not open for debate)

1. **If a session is working, it is BLUE. No matter what. Period, full stop.** Nothing outranks Working.
2. **The Gateway owns every state, centrally.** It is the ONLY thing that decides a colour.
3. **The Director decides nothing.** It reports FACTS - "I am working", plus a heartbeat.
4. Gateway-side rules sit on top of the facts: a session that terminates and has not pinged for about ten
   seconds may be set red - or gray if controlled.
5. **Gray-when-controlled is a LATER, more intelligent feature.** It applies ONLY to a RED (needs-you)
   controlled session. It must NEVER touch a working one. Do not build it now.

This **supersedes** the 2026-07-10 decision in issue #1286 ("a controlled worker always shows the recessive
Supporting colour"). The owner was explicit that the old decision is void. Do not restore it. Do not cite it.

## The proven defect (verified by the Architect against origin/main - build on this)

`src/CcDirector.Gateway.Contracts/SessionOrdering.cs`, in `BaseColor`:

```
if (controlled && !isRed)
    return "supporting";
```

Any controlled session that is NOT red returns `"supporting"` - slate, labelled "Sub-agent" by
`StateLabel`. The real activity state is **discarded**. That is why session 107 ("Stable Release - Manager")
rendered gray while 23 minutes and 56k tokens into real work.

The SECOND rule immediately below it is different and is NOT in scope to delete:

```
if (isRed && s.SessionRole == Worker)
    return "supporting";
```

That one suppresses a worker's RED, which is attention-routing (the need goes to its manager, not the
human). Per the law above, red-suppression is legitimate and stays for now. Blue-suppression is what
destroys information: blue never nagged anyone.

Also relevant: three near-identical grays exist and are indistinguishable in a small rail dot - slate
"supporting" (#64748B), light-gray on-hold (#9CA3AF), dark-gray exited (#6A6A6A). That is why the owner read
a working session as "on hold". Note it; do not redesign the palette in this item.

## Scope of THIS item

**Minimum:** a working session renders blue and labels "Working" on every surface, including a controlled
sub-agent. Remove the blue-suppression. Keep the red-suppression.

Ownership is already carried by a SEPARATE channel - the rail's role badges ("M", "A"). Colour must never
be used to say who owns a session. If removing the label leaves a gap, ownership belongs on the badge, not
the dot.

**Not in this item** (name them, do not build them): making the Director report Working as a fact to the
Gateway rather than computing anything; the Gateway ping/timeout rule; removing the remaining client
bypasses (issue #1241); the gray palette redesign. Those are the follow-on mission.

## The rules that are not negotiable

1. **Verify before you claim.** `git show origin/main:<path>` / `git grep <pattern> origin/main`. Never a
   commit message, never a memory, never a tree that may be behind.
2. **Work in a worktree cut from origin/main.** Never `git checkout -b` in the shared checkout.
   `git worktree add ../devthrottle-working-is-blue -b <branch> origin/main`
3. **Proof is a running build, not a green test.** Demonstrate on a slot Director that a controlled,
   working session shows blue "Working". A screenshot of the rail is the proof the owner asked for.
4. **No fallbacks.** Fix the root cause or fail loudly.
5. **Plain English, ASCII only.** No abbreviations, no jargon, no emoji, anywhere.
6. **Do not commit without the owner asking.** Do not merge. Do not tag.
7. Report to the Architect, not the owner. Ping at every milestone; never stall silently. Fleet messages are
   ONE line.

## Tests you will have to face honestly

`DesktopGatewayFoldAgreementTests` and `SessionOrderingTests` pin the CURRENT behaviour, including
`Assert.Equal("Sub-agent", SessionOrdering.StateLabel(...))`. Those assertions encode the superseded
decision. Update them to the new law - and say so plainly in the pull request. Do not weaken a test to make
a build pass, and do not delete an assertion without replacing it with one that pins the NEW rule.

There are **seven** test projects, not two. Core plus Gateway alone is a false green.

## The fleet

| Director | Port | Use |
|---|---|---|
| slot 6 | 7883 | Testing. Shared with the other Manager - coordinate through the Architect. |
| slot 2 | 7884 | Permanent. The Architect runs here. Do not disturb. |
| installed app | 7879 | v1.1.0. Never test against it. |

Build a slot: `powershell -ExecutionPolicy Bypass -File scripts\local-build-avalonia.ps1 -Slot 6`
Never kill a `cc-director*.exe` you did not launch.

## Definition of done

1. A controlled, working session renders blue and reads "Working" - proven on a running Director with a
   screenshot of the rail.
2. A controlled, red session still recedes (red-suppression intact).
3. Tests pin the NEW law, and the superseded assertions are gone.
4. A pull request is open and green. You do not merge it.
