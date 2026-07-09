# Fleet Identity: Session Naming and Addressing

Status: DRAFT. Date: 2026-07-09. Owner: f33d (policy + comms convention). Naming-enforcement
EXECUTION is hub/Gateway (naming is interpretation = the brain) - coordinate with the
streaming/fold tree; I own the policy and the addressing convention.

Two rules make a fleet legible: NAME every session, and ADDRESS by the short NUMBER, never the
GUID.

## The facts we build on (verified in code)

- Every session gets a short Number at creation: `SessionNumberAllocator`, range 100-999 (900
  slots). Issue #820.
- Numbers are unique among the ACTIVE sessions of ONE Director. The Director always allocates
  locally, so a session has a number even with no Gateway. The Gateway is the INTENDED authority
  for fleet-wide global uniqueness when reachable; the accepted #820 trade-off is that two
  sessions on DIFFERENT Directors can share a number when no Gateway coordinates them.
- A session also has an optional `CustomName` (human- or self-set) and a default display name
  (repo slug + 4-char id prefix, e.g. "devthrottle / f33d").

## Rule 1: Every session gets a real name (hub-enforced)

Problem: a session left on its default name (repo + id prefix) is illegible - the roster reads
like a list of hashes. Live example: this coordinator session was named "f33d" until it renamed
itself to "fleet-mgr roles+lifecycle".

Policy (my lane; the hub executes):

- GENERIC test: a name is generic if it is still the default pattern (repo slug and/or the 4-char
  id prefix) AND neither the human nor the session ever set `CustomName`.
- TRIGGER: still generic after N substantive turns OR M minutes of activity, whichever first.
  Proposal: N = 3 turns, M = 2 minutes. "Substantive" = the session has actually done work, not
  just launched.
- NAME GENERATION (brain / Gateway, because it is a judgment call): read the first user prompt +
  recent activity and produce a short, specific name (2-4 words; the TASK not the tool - e.g.
  "fleet roles design", not "claude"). Apply it via the Director rename verb
  (`PATCH /sessions/{sid}`).
- HELP before ENFORCE (proposal): first the hub SUGGESTS a name (the session or human may accept
  or override); if still generic after the trigger, it AUTO-APPLIES. A self-set or human-set name
  always wins and is never overridden.

## Rule 2: Address by NUMBER, never the GUID

Humans do not parse GUIDs; the wingman and inter-agent / internal comms must not use them.

- CANONICAL address in any human/agent/wingman text = the real NAME if present, else the NUMBER.
  The raw GUID never appears in human- or agent-facing output.
- The wingman says "111 wants you" (or "north/111"), never a GUID.
- Fleet messaging (`cc-devthrottle`) accepts the number as an address (today it takes id-prefix or
  name; add the number).
- The rail shows the number prominently so a human maps talk-to-row instantly.

Fleet-uniqueness requirement (hub / Gateway lane): for "send to 111" to be unambiguous across
machines, the Gateway must actually ENFORCE fleet-unique numbers (it is already the intended
authority, #820). Two shapes: (a) the Gateway hands out fleet-unique numbers, or (b) addresses are
machine-scoped ("north/111"). Recommend (a) as canonical, with (b) as the disambiguator when two
Directors are momentarily uncoordinated. Confirm whether the Gateway enforces this today; if not,
it is a hub work item.

## Rule 3: For the Architect and Manager seats, the name IS the role-of-mission

New requirement (Soren via the Architect, 2026-07-09). The full rule lives in
`mission-as-first-class-unit-of-work.md` (Addressing section, owned by the Architect); this doc
folds it in.

- A Mission has exactly one Architect and one Manager, so "the {role} of {mission}" is UNIQUE for
  those two seats. Their human-facing name SHOULD BE exactly that: "the Manager of the Lifecycle
  mission", "the Architect of Lifecycle". Sessions adopt role-of-mission names on the roster (e.g.
  "Lifecycle - manager", "Lifecycle - architect").
- Workers (N per mission) are not unique by role, so a Worker is addressed by its TASK.
- This extends Rule 2 one step: not just "number, not GUID", but for the two lead seats the name IS
  the role-of-mission. It never appears as a GUID, id-prefix, or bare number in any human- or
  Wingman-facing text.

## Open decisions for Soren

- N / M naming thresholds (proposal: 3 turns / 2 minutes).
- Help-then-enforce vs straight auto-name.
- Fleet-unique number enforcement by the Gateway (a) vs machine-scoped addresses (b). I recommend
  (a) canonical, (b) fallback.
