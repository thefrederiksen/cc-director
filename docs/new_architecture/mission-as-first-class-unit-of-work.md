# Mission: The First-Class Unit of Work

Status: DRAFT (design only, no code). Date: 2026-07-09.
Owner: c3df (mission model + naming). Sits ABOVE the role-behavior contract in
`session-roles-semantics.md` (owned by f33d) and consumes the identity/naming policy in
`fleet-identity-naming-and-addressing.md`. Ratified by Soren on the whiteboard, 2026-07-09.

This document defines the shared object a pod of sessions collaborates on, its naming, and the
role cardinality attached to it. It does NOT redefine role behavior or attention routing - that is
the role-behavior contract's lane. It adds ONE new idea: the Mission as a first-class object that
sessions attach to.

> **This is the OBJECT. For how a mission is RUN, read [`.claude/skills/mission/SKILL.md`](../../.claude/skills/mission/SKILL.md).**
>
> Two different questions, deliberately in two files. This one answers "what is a Mission, what
> attaches to it, what is it called". The skill answers "how does an Architect conduct one" - the
> four laws (ask up front then run alone; commit to the branch and never merge to main; a different
> agent inspects before anything reaches main; only the Architect merges), and how to write a brief.
>
> The rules live THERE and only there. Before that file existed they lived nowhere, so every mission
> brief restated them from memory - they drifted, and each brief read as though it were granting
> authority. One such brief reached main carrying a sentence that granted that mission open authority
> to commit: imperative voice, addressed to the reader, with the words limiting it to one branch and
> one week in a different paragraph. A brief describes the WORK; it must never grant a permission. If
> you are writing one, start at the skill.
>
> *(Paraphrased on purpose. Quoting that sentence would leave the exact imperative string in a
> permanent document, searchable, one retrieval away from being read as live - the failure it is
> cited as an example of. It is in git history on `backup/session-state-truth-2026-07-15`.)*

## The idea in one line

A **Mission** is the named unit of work a pod is collectively chartered to accomplish. Sessions
ATTACH to a Mission. That attachment - not the spawn tree alone - is what ties the pod together.

## Why a first-class Mission object (and not just the spawn tree)

Today the only relationship the fleet models is the spawn tree (`ControllerSessionId`: "X controls
Y"). A tree can only express strict hierarchy. It cannot express "the Architect and the Manager are
equals pointed at the same goal."

The Mission object supplies the missing horizontal binding:

- Architect, Manager, and Workers all ATTACH to one Mission.
- Architect and Manager are peers BECAUSE they share a Mission, not because one spawned the other.
- This is what lets the Architect sit BESIDE the Manager: you can rework the design with the
  Architect without stalling the running Manager, because neither controls the other - they are
  co-attached to the same Mission.

Without a Mission object, "Architect beside Manager" has nothing to hang on and collapses back into
a chain.

## Attaching a session that ALREADY EXISTS (issue #2387)

A Mission could originally only be joined in the instant a session was spawned
(`session spawn --mission <id>`). That made the feature miss the case it exists for. A mission's
shape is DISCOVERED as it runs - that is what makes it a mission rather than a task - so
attach-at-birth grouped only the work somebody had already planned, which is the work that least
needs grouping. The case that found it: a release push that began as one coordinating seat and grew
over a day into about a dozen - an architect and a manager, three review seats, three more for a
different question, an investigation seat, four independent gate reviewers commissioned one pull
request at a time, and a session fixing a defect one of them found. All one body of work. None of it
foreseeable at spawn. None of it representable.

The verb: `cc-devthrottle mission attach <session> <mission>` and `mission detach <session>`, over
`POST /sessions/{sid}/mission` at the Gateway and `POST /fleet/mission` on a Director.

### The rules, settled

Each of these was decided rather than left implied, because somebody hits every one of them and an
implied answer gets re-litigated at the worst moment.

1. **Attaching is a MOVE, not a one-way door.** A session that already carries a mission is
   re-pointed by the same command. Since the first classification of a session is always a guess
   about work still taking shape, a one-way attach would make every wrong guess permanent until the
   session was killed.
2. **The move is REPORTED.** The command names the mission the session LEFT. A move that reports
   only its destination hides that anything was displaced, which is how a session goes missing from
   the pod somebody is looking at.
3. **Detaching is supported.** No mission is the ORDINARY state of a session, so returning to it
   must not require inventing a mission to park the session in. A session that had no mission is
   told exactly that, rather than being told it was detached from nothing.
4. **A refused attach changes nothing.** A mistyped mission id leaves an existing attachment intact
   rather than clearing it - otherwise the failure looks like nothing happened until somebody goes
   looking for the pod.
5. **Attaching a controlling session does NOT bring its children by default; `--with-children`
   does.** A controller routinely commissions work that is not part of its own mission - a reviewer
   for one pull request, an investigation seat for something else - and a silent bulk re-parent
   cannot be undone in one step. With the flag, the walk is TRANSITIVE (children, their children,
   and so on): stopping at the first level would attach the Manager and leave the Workers behind,
   which reads as success while producing exactly the split view this feature exists to end. Every
   session it moves is NAMED, not counted.
6. **A spawned session INHERITS its controlling session's mission by default.** The fleet already
   records the controlling relationship, so this costs nothing and would have grouped the release
   push above for free - every one of those sessions was spawned by a seat already on the mission.
   An explicit `--mission` wins (stated intent beats a default), `--mission none` is the opt-out
   (spelled the way `--controlled-by none` is), and the inheritance is never silent: the spawn names
   the mission, names the session it came from, and says how to undo it.

### The workflow seat moves with the mission

**A Mission is not only a record. It is also a RUN of the built-in `mission` workflow**, and a
mission-scoped spawn seats the session on that run and records it in the run's participant ledger. The
seat - `WorkflowRunId` plus the workflow id and its PINNED version - is what decides the conduct the
agent was told to follow. So the mission link and the seat are two halves of one fact, and an attach
that moved only the link would leave a session **displayed under one mission while governed by the one
it left**, taking its conduct from a mission it is no longer in and still counted in that mission's
participant ledger. That is worse than an inconsistent label, and it would happen in exactly the case
this feature was built for. (Found by independent review of the first cut, which moved only the link.)

**The rule, and its one exception:**

- A seat that **is a run of the mission being left** belongs to that mission and **follows it**. On a
  move it becomes the destination mission's run; on a detach it is **cleared**.
- A seat the caller **chose independently** - spawned with an explicit `--workflow-run` that is not this
  mission's run - is **preserved untouched**. It was never the mission's to take.
- A session with **no seat** has nothing to preserve, so it simply gains the destination mission's seat.
- A destination mission with **no run of its own** (an UNGOVERNED mission, created while the owner had the
  mission workflow switched off) seats nobody, so the session moves unseated rather than keeping the old
  seat. A move must not smuggle a seat past a switch the owner deliberately turned off.

**Detach clears the seat rather than refusing.** Refusing to detach while a seat exists was considered
and rejected: every mission-spawned session is seated, so refusal would make detach impossible for
precisely the sessions that most need it. A session that has left a mission cannot still be governed by
that mission's run, and the coherent action - leave the run - exists. Refusal is the right answer only
where no coherent action does.

**Leaving is recorded, not erased.** The ledger marks the participant as having left (`LeftUtc`). That
the session *was* in that run is true and stays true.

**The decision is the Gateway's and is made in one place.** Whether a run belongs to a mission is a fact
about the run store, which only the Gateway holds; a Director asked to decide it would have to guess. The
Gateway sends the Director a finished answer - move the seat or do not, and which run to sit on - and the
Director applies it in the *same verb* as the mission, so the two can never land apart. This is also why
the Director's own `POST /fleet/mission` relays through the Gateway even for a session it hosts itself: a
second implementation of this rule would drift, and the drift would be invisible until somebody found a
session governed by a mission it had left.

**The limit, stated because it is real.** Moving the seat corrects the RECORD - what the fleet shows, what
governs the session, who the run lists. It cannot reach into a running agent's context and replace the
conduct text that was injected at birth. Only telling the session to fetch its conduct again does that,
and the command line says so every time a seat moves.

### The tenant boundary, which is the constraint that governs the design

Missions are TENANT-SCOPED and that was hard won (devthrottle_internal issue #1039 fixed a live leak
where `GET /missions` served every account's list to every account; a mission NAME is free text a
person typed - customer names, project names, people's names). Attach is the first WRITE that takes
a mission id from a caller and applies it, so it is exactly the shape that would put the leak back.

`POST /sessions/{sid}/mission` therefore follows `GET /missions/{mid}` line for line: resolve the
CALLER's own tenant server-side from the authenticated device key, REFUSE when no tenant is bound
(never fall back to a shared partition), and resolve the mission INSIDE that tenant. The mission is
resolved BEFORE the session is located, so the refusal is a property of the tenant gate alone and cannot
be confused with a Director being offline.

**A refusal must not reveal WHICH refusal it is, and that is a security property of the route rather than
a nicety of its error text.** "No mission has that id" and "that mission belongs to someone else" answer
identically - same status, same sentence, only the echoed id differing. If they diverged, attaching a
session to guessed identifiers would enumerate which missions exist in other accounts, one request at a
time, without ever reading one. The test that holds this compares the two answers TO EACH OTHER rather
than to a fixed string, so a later edit cannot reword one and leave the other behind.
`MissionAttachRouteTenantScopingTests` holds it to that, pairing every refusal with a permitted
request in the same Gateway and asserting that no `attach-mission` command reached the Director.

The Gateway sends the RESOLVED NAME down with the id and the Director stamps it directly, exactly as
it does for a mission-scoped spawn. A Director must not re-validate against its own mission store:
that is a different (single-tenant, per-machine) set, so a mission that is real and owned would be
rejected for being absent from the wrong store - the failure issue #1548 fixed on the spawn path.

## Naming: Mission -> Task

- The shared root the pod attacks = a **Mission**.
- The single piece a Manager hands to one Worker = a **Task**.

Chosen after a prior-art survey of both multi-agent frameworks and mature work-management tools:

- **Task** is the universal, baggage-free unit-of-work primitive - CrewAI, Magentic-One, MetaGPT,
  LangGraph, Jira, Azure DevOps, and Asana all use it for exactly this.
- **Mission** is the one root name that inherently means "a crew is collectively dispatched to
  accomplish one objective," has a real decompose-into-tasks precedent (robotics and defense mission
  planning literally break a mission into tasks assigned to assets - structurally identical to a
  Manager decomposing a Mission into Tasks for Workers), collides with no mainstream work-management
  hierarchy level, and reinforces the "Mission Control" brand.

Rejected alternatives and why:

- **"Work item"** - this is the industry umbrella term for ALL work types (Azure DevOps and Jira
  define a single Task AND an Epic as both being "work items"), so it blurs the root and the piece
  into one word. It is the most confusing option, not a neutral one.
- **"Objective"** - collides with Anthropic's use of "objective" for the delegated piece, and
  carries objectives-and-key-results baggage.
- **"Epic / Story"** - locks the vocabulary into agile ceremony.

## Role cardinality per Mission

A Mission has exactly:

- **One Architect** (optional). The DESIGN authority: it (a) RECOMMENDS to the Manager and
  (b) maintains the architecture / design documentation - that is its whole mandate. It does NOT
  drive implementation, and it does NOT own the human-facing status / attention channel. It never
  pushes needs-you or status to the human; it engages the human ONLY in a design conversation the
  human INITIATES, then recommends to the Manager and edits the docs. Co-equal with the Manager on
  design (neither controls the other), but the Manager alone owns execution and the human-facing
  channel. A simple Mission can run with no Architect (just Manager plus Workers). (This AMENDS the
  older "Architect is human-facing, red allowed / never suppressed" definition in
  `session-roles-semantics.md` - Soren, 2026-07-09.)
- **One Manager** (required once work is delegated). The human-facing coordinator. Owns the
  decomposition of the Mission into Tasks and the supervision of the Workers. Must stay available -
  it is not interrupted by "I am done" from a Worker; it learns Worker status by reading and
  summarizing them (see the role-behavior contract).
- **N Workers**. Subordinate, never surface to the human, escalate UP to the Manager. A Worker's
  job type (developer, quality assurance, and so on) is decided by the agent skill at runtime and is
  deliberately NOT encoded in the role.

Never two Architects and never two Managers on one Mission - single design authority, single
coordinator. This matches every serious system surveyed (Magentic-One's one Orchestrator,
Anthropic's one lead agent, CrewAI's one manager in the hierarchical process).

## What sizes a Mission: the goal, not the labor

A Mission is defined by its GOAL, not by how much work it takes or how many sessions do it (Soren,
2026-07-09). A large goal does NOT become several Missions - it becomes ONE Mission with more Tasks
and more Workers under the single Manager. A Manager scales by adding Workers, never by spawning
peer Managers.

You split into a sub-Mission ONLY when the work contains an INDEPENDENT sub-goal that genuinely
deserves its own Architect, Manager, and Workers (the nesting case). Labor size, or the fact that
work spans two sessions, is NOT a reason to declare two Missions - that is a division of LABOR,
which is what Tasks are for, not a division of MISSION.

(This corrects an earlier draft of this doc that sized a Mission by "what one Manager can supervise"
and split on labor. The right rule: split on an independent sub-goal, not on size.)

Worked example (this project): "fix the session lifecycle" is ONE Mission. The plumbing (role data,
schema, auto-naming) and the visible features (role badge, the spawn command, the role flag,
attaching a session to a Mission) are TASKS inside it - even though different sessions build them.

## Nesting: REMOVED 2026-08-07. Missions are flat.

> **This section describes a feature that no longer exists.** Nesting was specified here, built
> (`Mission.ParentMissionId`, a parent argument on `MissionStore.Create`, parent validation on
> `POST /missions`, and a tenant-scoping test for it), and then **never used once** - every Mission
> the fleet ever created had a null parent. Soren decided to drop it on 2026-08-07.
>
> **Why remove it rather than leave it lying there.** An unused field is not free. It has to be
> understood by everyone who reads the type, kept correct in every store and route that touches it,
> carried through every migration, and reasoned about by every feature built afterwards - and mission
> state, rename, and numbering were all about to be built on top of it. It also widened the create
> route's attack surface for nothing: a parent is a caller-supplied reference INTO the mission set, so
> it needed its own tenant guard and its own test to prove another account's mission could not be
> named as one. Deleting the field deleted that whole class of question.
>
> **What this does NOT change:** the rule above about what sizes a Mission still holds - a large goal
> is still ONE Mission with more Tasks and more Workers, and labor size is still not a reason to
> declare a second Mission. What is gone is only the modelled parent/child LINK. Two related missions
> today are simply two missions.
>
> **The original design is kept below rather than deleted**, so that if a real case for sub-Missions
> turns up, the reasoning is here to restart from instead of being re-derived. If it comes back, the
> tenant-scoping test comes back with it.

A Worker may itself spawn Workers. Under "one Manager per Mission," that Worker does not become a
second manager of the parent Mission - it becomes the **Manager of a child Mission**. A large effort
is therefore a TREE of Missions, each with its own single Architect, single Manager, and Workers,
and the one-Manager-per-Mission invariant holds at every level.

## How roles attach (consistency with the derived-role model)

The role-behavior contract derives Manager-versus-Standalone from the spawn graph: a non-worker
becomes a Manager the instant it owns a live Worker. The Mission object is consistent with that: a
Standalone session is simply a session not yet attached to a Mission with Workers. When it opens a
Mission and delegates the first Task, it occupies that Mission's Manager seat. The Mission adds the
Architect seat and the shared attachment; it does not change how Manager-versus-Standalone is
derived.

REFINEMENT (from first live use, 2026-07-09): a Mission ASSIGNS explicit seats - exactly one
Architect seat and exactly one Manager seat. A session can occupy the Manager seat of a Mission
BEFORE it has spawned any Worker (it is the human-facing owner of that Mission's build, and it will
spawn the Workers). The derived-Manager fact (owns one or more live Workers) then simply ACTIVATES
the seat the Mission already named. The two views agree once Workers exist; before that, the
assigned Mission SEAT is the source of truth, not the still-Standalone derived graph. This is how a
Mission can have a named Manager while its build is still blocked on a dependency.

## Addressing: name a session by its Role-of-Mission, never by an internal id

The human-facing way to refer to a session is its ROLE within its MISSION - not an internal id, a
GUID, or a 4-character prefix (Soren, 2026-07-09). Say "the Manager of the Lifecycle mission" or
"the Architect of Lifecycle", never "f33d" or "111".

- Because a Mission has exactly ONE Architect and ONE Manager, "the {role} of {mission}" is a
  UNIQUE, unambiguous address for those two seats - a direct payoff of the cardinality rule. The
  mission qualifier also disambiguates across missions: "the Manager of Lifecycle" and "the Manager
  of the Stream mission" are two different sessions, stated plainly.
- Workers are N per Mission, so a Worker is addressed by its TASK, not just its role: "the worker
  running the {task} task on Lifecycle". This is exactly why the auto-naming policy names a Worker
  from its task.
- The internal id/number still exists for MACHINE addressing (the CLI, the API) but must never
  appear in human- or Wingman-facing text. This extends
  `fleet-identity-naming-and-addressing.md` (address by name/number, not GUID) one step further:
  the human-facing NAME itself should be the Role-of-Mission.

### Display-name convention (ratified by Soren, 2026-07-11)

The session DISPLAY NAME encodes Role-of-Mission in a fixed, sortable order - mission first,
role second, joined with " - ":

- `Gateway Connection - Architect`
- `Gateway Connection - Manager`
- `Gateway Connection - Worker - connect panel` (a Worker appends its Task at the end,
  consistent with "a Worker is addressed by its Task" above)

Rules:

1. Mission name FIRST, so sorting the session list by name groups every session of one Mission
   together, Architect and Manager adjacent, Workers under them.
2. The repository is NEVER part of the name - the session list displays the repository in its
   own column, so repeating it in the name is noise that pushes the meaningful part off-screen.
3. Session ids and numbers are NEVER part of the name (they are machine addressing, per the
   rule above).
4. A solo session with no Mission is named for its work ("Clean up stale branches"), again
   without the repository.

The auto-naming build (`automatic-session-roles-naming-spec.md`, Chunk 3 - the composed
role-flavored name at birth) must compose names to this convention.

## Wingman must speak the model, in plain engineering English

The Wingman has to understand the Mission/role model so it can talk about sessions correctly, and it
must speak plain engineering English (Soren, 2026-07-09): "the Manager of Lifecycle needs you", NOT
"111 wants you", and NOT Silicon Valley jargon (no "woodshedding", no "dogfooding", no smartass
register). The Wingman brain (moving to the Gateway - the brain lane) owns implementing this; this
Mission/role model plus the two language rules are the contract it consumes. See memory
`wingman-plain-language-role-addressing`.

## Attention hard rules: who may surface, auto-hold, and orphan handling

These three rules are the authoritative attention decisions for the Mission/role model (settled by
the Architect at Soren's request, 2026-07-09). The role-behavior contract and the Gateway fold
IMPLEMENT them; this doc is where they are DECIDED.

### Rule 1 (hard invariant): only a Manager, a Standalone or an Architect ever surfaces to the human

A WORKER never surfaces a needs-you to the human - not when blocked, not when done, not when
orphaned. A worker's attention ALWAYS routes to a Manager: its own while alive, or the fallback Hub
Manager if its own dies (Rule 3). The ONLY sessions that ever enter the human's needs-you queue are
Managers and Standalone sessions. This is structural (the fold suppresses a worker's red toward the
human; enforce in the transport, not the prompt), not a request a worker can make. CONFIRMED already
in the design and built for the alive-manager case.

SETTLED (Soren, 2026-09-06): the ARCHITECT IS A HUMAN-FACING SEAT and pushes exactly like a Manager.

> "parking the architect seat is wrong. the architect is always the session i talk to."
> "no the architect should push to me it is what i talk to."

So the human-facing status / attention channel belongs to the MANAGER, the STANDALONE and the
ARCHITECT. Only a Worker with a live supervisor - and a run started by a schedule, which has nobody
at a keyboard to report to - stays out of the human's queue.

SUPERSEDED AMENDMENT (Soren, 2026-07-09), kept because deleting it invites somebody to derive it
again from the surrounding prose: it said the Architect likewise does NOT push needs-you or status to
the human, differing from a Worker only in that the human MAY open a design conversation with it (a
PULL) and it never pages him (no PUSH). That reached this document and `session-roles-semantics.md`
in July, was built on 2026-09-03 (commit 2a8679007, #2667), and was reversed on 2026-09-06 once the
owner had watched it running - the seat he addresses cannot be the seat that is silenced.

The machine-checked statement of this rule is the supervision table in
`docs/new_architecture/session-roles-semantics.md`, which a test parses and compares against
`SessionOrdering.IsSupervised`. If this prose and that table ever disagree, the table is the one the
code is held to; fix both in the same pull request.

### Rule 2 (revised): auto-hold is for Workers; a Manager is never auto-muted

Refined by Soren, 2026-07-09, correcting an earlier draft that auto-held Managers too.

- WORKER: when a worker is done / idle with nothing pending for its manager, it AUTO-HOLDS - it
  drops from its manager's highlight and any queue. Safe because a worker has no human channel
  anyway; this only tidies the manager-facing view. (A worker that is genuinely blocked / needs a
  decision is NOT held - it flags its manager; see Rules 1 and 3.)
- MANAGER / STANDALONE: NOT auto-suppressed, ever. A Manager raises "need you" entirely by its OWN
  judgment - to get a decision to continue, OR simply to REPORT an update to the operator. Both use
  the SAME existing "need you". Involving the operator when the manager judges it worthwhile is the
  POINT of a manager, not clutter. The manager owns the balance between informing and bothering -
  that is judgment, not a mechanism to automate.
- On Hold for a Manager is a post-update TIDY, not a muzzle: it applies only AFTER the operator has
  been told and there is nothing left to act on - operator-driven by default, or an automatic
  "nothing-left" cleanup ONLY once the operator has actually seen the update. Never auto-hold a
  Manager that still has an un-surfaced update or a pending ask.

### Rule 2a (explicit): NO NEW STATES - it is queue routing, not a new signal

The single existing "need you" covers BOTH "I need a decision" AND "here is an update" (Soren
explicit, 2026-07-09). Do NOT add an "update" state, an "informational" state, or any new signal.
The distinction between a decision and an update lives in the Manager's message content, not in a
new machine state.

Any "stays in the operator's queue vs auto-holds" behavior is QUEUE ROUTING computed over the
EXISTING Waiting state plus the existing pending-surface signal - it is never a new user-facing
state. A Manager's DELIBERATE surface counts as a pending surface - a question the manager chose to
ask AND a report the manager chose to send both KEEP the manager in the operator's queue until the
operator has seen it. Only a Manager that goes idle having surfaced NOTHING is eligible for the
nothing-left tidy. We never auto-hide something a Manager deliberately put up.

### Rule 3 (decided by Soren, 2026-07-09): a dead Manager is an EXCEPTION - escalate to the operator

A Manager dying is an ERROR / exception state, and Soren's rule for exceptions is: ALWAYS involve the
operator. So we do NOT auto-reassign an orphaned worker to a Hub Manager - that would add always-on
machinery AND mask the real problem (a Manager just died; the operator should know). When a Worker's
Manager dies:

- Worker IDLE / DONE: auto-hold it (Rule 2). Finished work is not an exception; its result is
  collected later and nobody is raised.
- Worker BLOCKED / mid-task: it ESCALATES TO THE OPERATOR. The fold / brain surfaces it WITH A CLEAR
  EXPLANATION - "the Manager (<name>) you were working for is gone; here is the task; here is where
  you are stuck" - and the operator goes in and cleans up (resume it, hand it to another manager,
  reassign it, or stop it). It surfaces LOUDLY and WITH CONTEXT, never as a bare red dot and never
  silently.

This is a DELIBERATE exception to Rule 1 (a Worker never surfaces): Rule 1 governs NORMAL operation;
a dead Manager is not normal, and an exception always reaches the operator. The orphan is no longer
anyone's subordinate, so surfacing it is correct.

Design goal (Soren): MINIMIZE how often this happens. Orphaning should be rare - Managers should be
durable / resumable so a crash does not strand workers, and a Manager shutting down CLEANLY should
deal with its workers FIRST (finish, hand off, or stop them) so orphaning only ever results from a
true crash. This escalation is the safety net for the rare true-crash case, not an everyday path.
Orphaned workers must remain persisted + resumable so the operator can actually resume them.

MISSION-LEVEL orphan (extension, from live use 2026-07-09): the same exception principle applies when
a MISSION's MANAGER exits, not just a Worker's. If the Manager of a Mission exits, the Mission's
in-progress work - its branch, its pending merge to main, and its remaining Tasks - becomes ORPHANED
and unowned. This ESCALATES to the operator to reassign or adopt (assign a new Manager to the Mission,
or drive the remaining work directly). It must not silently stall - the committed work and the pending
merge are exactly what gets stuck. Real instance: the Director-Gateway STREAM mission's Manager exited,
orphaning its pending merge to main (which ships OTHER missions' committed work too), its next schema
increment, and the launcher-persistent-join dependency. RESOLVED 2026-07-09: the Lifecycle mission
ADOPTED this orphaned Stream domain (Soren's assignment - the mission that needs it takes it); see the
Multi-computer start/stop section.

## Visual language (in progress)

Decisions as they settle (started 2026-07-09, Architect + Soren):

- RAIL vs MAP split (Option A, Soren): the rail stays a FLAT, hand-ordered list - one small role
  ICON per row, no grouping and no indenting, order stays whatever the operator set (honoring the
  never-auto-reorder-the-rail rule). ALL relationships - the Mission grouping and the
  manager-to-worker links - are drawn in the COCKPIT MAP, not the rail. The rail answers "which
  session do I click"; the map answers "how do these fit together".
- Role marker = grey LETTERS: M = Manager, W = Worker, A = Architect; Standalone = no marker.
  DECIDED 2026-07-09 (Soren): keep the already-built grey letters - most legible at small size, zero
  rebuild, and letters are NON-COLOR so they honor role-by-marker / status-by-color. The earlier
  crown / gear / triangle pictorial proposal was over-design and is dropped.
- MAP layout = Mission as a CONTAINER (Soren): the Mission is a box / card, and being INSIDE the box
  is what "attached to the Mission" means. Inside, the Architect (A) and the Manager (M)
  sit side by side at the TOP as peers; the Workers (W) hang BENEATH the Manager. (The "nested
  sub-Mission is a smaller box inside" part of this layout is MOOT - nesting was removed on
  2026-08-07, see the Nesting section above; boxes never contain boxes.) Status / attention
  COLOR still applies to each session node inside the box. Mission-to-Mission dependency edges draw
  as arrows between boxes. The rail stays flat (Option A); the map is the ONLY place the pod
  relationships are drawn - rail = "which session do I click", map = "how do these fit together".

## Mission scope and work routing (no default-by-name)

Surfaced from live use, 2026-07-09 (the Lifecycle manager): work kept being routed to a Mission
because its NAME sounded close, not because it was in scope (release / installer / self-update work
pushed at "Lifecycle"). Two rules:

1. A Mission has an explicit SCOPE - the written boundary of what it owns. Work is routed to a
   Mission by SCOPE, never by name similarity. A Manager DECLINING out-of-scope work is CORRECT
   behavior, not obstruction. So a Mission's scope must be written down (in its doc / the Manager's
   charter) precisely so routing is by scope, not a guess at the name.
2. HOMELESS work is ASSIGNED, never DEFAULTED. Cross-cutting work that no existing Mission owns -
   for example release / installer / self-update / deploy infrastructure (issue #1186, ffmpeg
   bundling) - is a homeless-ownership gap. It must be explicitly assigned by Soren / the Architect
   (given its OWN Mission or a named owner) and must NEVER default onto the nearest-sounding Mission.
   When a Manager meets homeless work it DECLINES and escalates the ASSIGNMENT question up (to the
   Architect / Soren); it does not absorb it.

## Naming note: "Lifecycle" means SESSION lifecycle

The "fix the lifecycle" Mission is specifically SESSION lifecycle - session roles, naming,
start / stop, and attention. The bare word "Lifecycle" collides with APP / RELEASE lifecycle
(packaging, deploy, installer, self-update), which is a DIFFERENT concern and explicitly NOT in this
Mission's scope. Recommendation to Soren: optionally clarify the Mission name to make scope legible -
e.g. "Session Lifecycle" or "Session Roles" - to stop release/deploy work being misrouted here.
Until Soren decides, the name stays "Lifecycle" with this scope note as the guard.

## Multi-computer start / stop (design)

Requirement from Soren (2026-07-09): starting (and stopping) sessions across computers is a
FIRST-CLASS capability. Design:

- "Which computer" is an OPTIONAL first-class parameter on START, DEFAULTING to LOCAL (the requesting
  agent's own machine). "Start a session" with no computer = start on the machine the agent is on
  (common case, unchanged). To target another machine you NAME it: "start a session on <machine>".
- ROUTING mirrors existing plumbing + the cron / schedule precedent (which targets a MACHINE, not a
  specific Director): LOCAL = direct to the agent's own Director; REMOTE = via the Gateway (it knows
  the fleet roster) to a Director on the target machine, on the first available Director there.
- ADDRESS the target by MACHINE NAME (human-legible roster name), consistent with the addressing
  policy (names, not GUIDs).
- NO Director on the target: auto-launch one via cc-launcher (as cron already does), then spawn -
  enabled by the launcher requirement below.
- STOP needs NO computer parameter: a session's machine is part of its identity, so STOP addresses
  the SESSION and routes to its home machine automatically. Machine-targeting is START-only.
- CROSS-MACHINE PODS: a Manager on machine A may own a Worker on machine B - the two-layer attention
  model already works fleet-wide.

DECISION (Soren, 2026-07-09) - offline / unreachable target: FAIL FAST + FAIL LOUD. If the target is
off or unreachable there is nothing we can do (we cannot power on machines), so the start FAILS
IMMEDIATELY with a clear error: "cannot start - computer <X> is off / unreachable". NO queue-until-wake,
NO silent fallback to local. Matches the no-fallback / no-silent-degrade law. (Queue-until-wake was
considered and explicitly rejected for v1; possible future addition.)

### Hard dependency: cc-launcher must persistently JOIN the Gateway

Requirement from Soren (2026-07-09): cc-launcher - the tiny tray app that auto-starts on Windows boot
and is ALWAYS running (unlike cc-director) - must change its Gateway connection from REST-both-ways to
the SAME persistent JOIN / stream the Director now uses, so the launcher is ALWAYS connected.

WHY: an always-connected launcher is what lets the Gateway COMMAND a machine to START cc-director when
none is running - the ENABLER for "start a session on computer X" when X has no Director:
Gateway -> always-connected launcher -> launch cc-director -> spawn session. Without it a Director
cannot be started remotely, so remote-start on an idle machine is impossible.

UNIVERSAL LAUNCHER (Soren, 2026-07-09): EVERY machine runs a launcher - including this machine and the
machine that hosts the Gateway; there is no special-case machine without one. That uniformity is the
point - the launcher-persistent-join is the SAME mechanism on every machine, so the whole fleet behaves
identically. Practical consequence: the launcher-join + remote-start flow can be built and tested
entirely on THIS machine's own launcher - no second physical machine is needed to verify it.

OWNERSHIP - RESOLVED (Soren, 2026-07-09): the LIFECYCLE mission ADOPTS the streaming work. Rather than
hand the orphaned Director-Gateway STREAM domain to a stranger, the mission that NEEDS it takes it -
Lifecycle's remote-start depends on the launcher-persistent-join, and shipping Lifecycle's own
roles + badge work depends on the Stream-branch merge. So the Lifecycle mission now OWNS the
launcher-persistent-join plus the rest of the orphaned Stream domain. The Stream pod had already exited
(no leftover streaming sessions to close - verified 2026-07-09). Implementation mirrors how the
Director's own stream client was built (same pattern, second client = the launcher). PRIORITY ORDER
(Manager): (1) MERGE the Stream branch to main - ships the already-built roles + badge to the real app;
(2) launcher-persistent-join - unblocks remote-start; (3) missionId / missionName schema, then
streaming Phase 4b.

## What this document deliberately does NOT do

- It does not define role attention, color, or escalation mechanics - that is the role-behavior
  contract (`session-roles-semantics.md`, f33d) and the single Gateway color fold.
- It does not define naming thresholds or addressing - that is
  `fleet-identity-naming-and-addressing.md`.
- It does not design the visual language (rail glyph for the Architect, the cockpit Mission map).
  That is the next whiteboard item, listed below.

## Open items

1. Visual language: the rail already has crown (Manager) and gear (Worker); the Architect needs a
   glyph, and the cockpit map needs to render the Mission as a shared node with the pod arranged
   around it (the whole reason the Mission is an object). Design this next.
2. Reconcile the forced Worker `--dangerously-skip-permissions` posture in the role-behavior
   contract against the "do not police permission posture - it is the user's choice per session"
   principle. These collide for Workers; decide before build.
3. Mission persistence - DECIDED (c3df + f33d, 2026-07-09): a Mission is its OWN persisted record
   (fields `missionId` + `missionName`) that sessions attach to - not merely an attachment field -
   so it survives a Manager restart and anchors the cockpit map. SCHEMA OWNERSHIP + SEQUENCE: the
   session record and `NewSessionRequest` are owned by c9f9a8e3 on branch
   `feat/director-gateway-stream-1a`. `SessionRole` lands FIRST (in flight now); `missionId` is the
   NEXT increment on the same files, single writer, one-thing-at-a-time - it must NOT be added
   simultaneously or it collides with the in-flight SessionRole edit. Final priority/ordering is
   Soren's call.

## One Mission, tasks built by different sessions

Per Soren's decision (2026-07-09) the lifecycle is ONE Mission. Some of its Tasks - the plumbing
(the `SessionRole` data, the schema, auto-naming) - are currently built by a session whose primary
work is a separate, broader Director-Gateway streaming rework; the visible-feature Tasks (badge,
spawn command, role flag, Mission-attach) are the Lifecycle Manager's own build. Different sessions
building different Tasks is a division of LABOR inside the one Mission, not a reason to split it.

Missions CAN depend on other Missions in general (a real capability the cockpit map should
eventually show as a dependency edge), but the lifecycle is not itself two Missions - it is one
Mission whose Tasks happen to be built across sessions.
