---
name: terminology
description: The words DevThrottle uses and what each one means - session, mission, task, workflow, run, team, the five roles, supervisor, parent, participation, snooze, agent. Use when writing an issue, brief, commit message, document or code comment, when unsure what to call something, or when you meet an older name in the code. Triggers on "/terminology", "what do we call", "what is the right word", "glossary", "vocabulary", "naming", "is it hold or snooze", "controller or supervisor".
---

# DevThrottle terminology - the words we use, and what they mean

One word per idea, one idea per word. These are the definitions everything else in DevThrottle
uses - the product, the documentation, the workflow conduct, and every agent working in this
fleet. If you are writing an issue, a brief, a commit message, a document, a code comment, or a
message to another session, use these words in these meanings.

The rule behind the list: we do not have to copy the industry, but we must not use a word that
already means something DIFFERENT in it.

## The work

- **Session** - one running coding agent: a process, on a machine, in a repository, that someone
  is talking to. The atom of the system. Never call a session an "agent".
- **Mission** - an undertaking: WHY we are doing something, WHO is working on it together, and the
  TASKS it breaks into. One session or many. A mission HAS a goal; it is not itself a goal. Every
  mission must state its why.
- **Task** - one piece of a mission, handed to one worker. A mission has several. (The word is
  settled; a Task record is not built yet, so do not write as though the system stores one.)
- **Workflow** - a reusable, versioned, published way of working. The HOW, where a mission is the
  WHAT. A mission may run any workflow.
- **Run** - one execution of a workflow, in service of a mission. It carries what the workflow
  promised and the evidence for each promise.
- **Team** - the workflow where an Architect settles the design, a Manager drives the phases, and
  Workers build; and also the sessions themselves when they are on one mission together.

Mission and Run are two records and that is deliberate. A Mission is a durable statement of
purpose that holds several tasks and can outlive any single execution; a Run is one mechanical
execution. Neither is a duplicate of the other.

## Roles - what a session is for

Five, and no others. Four are settable today; Reviewer is agreed but not yet recordable - see the
last section.

- **Standalone** - works alone, faces the human. The default.
- **Architect** - settles the design and writes the phases down. Must be declared; it cannot be
  inferred from who spawned whom.
- **Manager** - drives the phases and supervises the workers.
- **Worker** - builds.
- **Reviewer** - a different session from the one that wrote the work, checking it before it lands.

An **Inspector** is a Reviewer carrying one extra requirement: it must be from a DIFFERENT AGENT
FAMILY to the people who did the work - if the fleet is Claude Code, the Inspector is Codex. Keep
using the word; it is not a sixth role, because the constraint is already proved by the agent kind
recorded on every session and every run participant.

## How sessions relate

- **Supervisor** - the session that supervises another. A live relationship, and it changes how the
  supervised session is displayed.
- **Parent** - the session that spawned another. A historical fact, with no effect on display.

These are genuinely two different things. They carry the same id on an ordinary spawn and diverge
when a session deliberately starts a human-facing peer: no supervisor, but still agent-started.

- **Participation** - a session's membership of a run, either active or ended. Do not say "seat":
  in any commercial context a seat is a paid licence.

## State

- **Snooze** - suppressing a session's demand for attention, either now or as soon as it stops
  working. A snooze asked for while the agent is still working is a **snooze requested**; it lands
  when the work stops. Do not say "hold" or "parked".

## The machinery

- **Agent** - the coding agent TOOL: Claude Code, Codex, Gemini, Grok. It is what you run. A
  session is one running instance of it. This is the single most common word to get wrong.
- **Director** - the application on each machine that drives that machine's sessions.
- **Gateway** - the cloud service that aggregates every machine and owns every display ruling. It
  is strictly a control plane rather than a gateway; describe it that way when it matters.

## Two known exceptions, stated on purpose

- **Worker** collides with Temporal and Kubernetes, where a worker is a PROCESS, not a role. We
  keep it anyway: it is in the shipped workflow steps, the role constant, the mission conduct and
  the command line, and it is a word we say out loud. Accepted, not overlooked.
- **Group** is retired. It was an older way of clustering sessions in the rail; Mission does that
  job. Do not use it in new work.

## Words in the code that have not caught up yet

The code still uses some older names. When you read them, translate; when you write NEW code or
prose, use the word on the left.

| Say this | The code may still say |
|---|---|
| Supervisor | `Controller`, `ControllerSessionId`, `--controlled-by` |
| Snooze | `HoldState`, `DeferredHold`, "parked" |
| Participation | "seat" |
| Team (the workflow) | the `mission` workflow |
| Reviewer | nothing - the role does not exist yet |

Do not "fix" these opportunistically in unrelated work; each is a deliberate rename with its own
cost, and a half-applied rename is worse than either name.
