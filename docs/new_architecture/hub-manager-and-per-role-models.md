# Hub Manager and Per-Role Model Configuration

Status: DRAFT (design). Date: 2026-07-09. Ownership: coordinated between the hub/Gateway tree
(overnight manager, c9f9a8e3) and f33d. COORDINATION REQUIRED before implementation (Soren's
directive). Documented per Soren's request; implementation is pending the ownership split and a
build go-ahead.

Two related additions to the fleet framework.

## Part A: The Hub Manager (a ConPTY-hosted fleet manager)

Concept: the fleet gets a top-level MANAGER that runs as a real ConPTY session at the hub (the
Gateway / brain box), not just an implicit background brain. It is the fleet's Queen: it
supervises worker sessions, it is a seat that surfaces to the human, and it has a
large-language-model ASSISTANT it can call whenever it wants - an on-demand side-call brain,
distinct from whatever agent runs in its own terminal.

Why a ConPTY session (not only a background service):
- It is a first-class Manager in the `SessionRole` model - it can own workers
  (`ControllerSessionId`), appears on the roster, and is addressable by number/name like any
  session.
- A ConPTY keeps it agent-agnostic custody, the same as every other session - the hub manager is
  "just a session" that happens to hold the Manager role and a dedicated model.
- The on-demand LLM assistant is the warm-brain-per-fleet idea (see
  `docs/architecture/DIRECTOR_DUMB_WRAPPER_TARGET.md` section 4.1 and its cost rationale): one warm
  strong model the manager calls for judgment (summarize a worker, decide, brief the human),
  instead of cold per-Director side-calls.

Relationship to existing pieces:
- Wingman brain: the hub manager's LLM assistant overlaps the Wingman "decide" brain that
  DIRECTOR_DUMB_WRAPPER 4.1 / track B1 moves to the Gateway. The hub manager is likely the SEAT
  that hosts or drives that brain.
- SessionRole: the hub manager is the canonical Manager; workers report up to it; only it (and
  Standalone sessions) surface red to the human. See `session-roles-semantics.md`.
- Identity: it is a normal named + numbered session. See
  `fleet-identity-naming-and-addressing.md`.

Ownership (proposed, pending the manager's reply): the hub/Gateway tree owns the ConPTY hub-manager
plus its LLM-assistant integration and how it consumes models (their brain lane). f33d owns the
per-role config schema, the settings UI, and the documentation.

Open design questions (settle with the manager):
- ONE hub manager per fleet, one per machine, or spawned on demand? (Lean: one per fleet at the
  Gateway box, spawnable and tear-down-able.)
- Does its ConPTY run a specific agent CLI and separately call the LLM assistant, or IS the
  assistant its agent?
- How does the human talk to it - a dedicated Cockpit surface, or as a normal session?

## Part B: Per-Role Model Configuration

Today (verified in code): `WingmanModelConfig`
(`src/CcDirector.Core/Configuration/WingmanModelConfig.cs`) resolves a hosted model for the wingman
using only two GENERIC tiers - `WingmanModelRole { Thinking, Fast }` - stored in `config.json` as
`brain_model` / `brain_model_fast`, falling forward to provider defaults (glm-5.2 / gpt-5.5) when
unset or a stale Claude alias. The AI tab exposes a single "Wingman model" dropdown.

Target: name the CONSUMER, not just the tier. Each large-language-model consumer role gets its own
model binding; thinking/fast remain the generic fallback when a role model is unset.

- Roles to define now: a WINGMAN model and a HUB-MANAGER model. (Other consumers exist -
  transcription cleanup, TTS - but the two Soren named are the first-class ones to define.)
- Config shape: keep `brain_model` / `brain_model_fast` as the generic thinking/fast defaults; add
  per-role keys (e.g. `wingman_model`, `hub_manager_model`, with optional `_fast` variants).
  Resolution order: the role's model if set, else the generic thinking/fast, else the provider
  default. This extends `WingmanModelConfig.Resolve(mode, role)` from a two-value enum to a
  role-keyed lookup, keeping the existing fall-forward guard (never send a stale Claude alias to
  the hosted proxy).
- AI settings page: one dropdown per named role ("Wingman model", "Hub-manager model"), each
  defaulting to "(use the thinking/fast default)", populated from the live hosted-model catalog as
  today.

Ownership (proposed): f33d owns the config schema + the AI settings-page UI; the hub/Gateway tree
owns consuming the hub-manager model in the brain.

## Sequence (Soren's directive)

1. COORDINATE with the overnight manager - DONE (message sent; awaiting the ownership reply).
2. DOCUMENT - this doc + a GitHub issue (this step).
3. IMPLEMENT - only after the ownership split is settled AND Soren greenlights the build.
