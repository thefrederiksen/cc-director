# MISSION: DevThrottle speaks French and Spanish

**Architect:** session `6c69987b` (Devthrottle Multilingual Research)
**Branch:** `mission/multilingual`, worktree `D:\ReposFred\devthrottle.wt-multilingual`
**Repo:** cc-director (`thefrederiksen/devthrottle`). Issues live in the PRIVATE repo
`thefrederiksen/devthrottle_internal`.

Read the conduct first: `cc-devthrottle workflow instructions mission`. This file describes the
WORK. It grants nothing.

---

## What is already done (do not redo)

| Issue | State |
|---|---|
| #1005 hide the AI tab | MERGED to cc-director main (`696b84ab`) |
| #1006 register FR/ES voices | MERGED to devthrottle_internal main |
| #1007 speech lab | MERGED, live, working |

Four Kokoro voices are registered and serving: `ff_siwis` (French), `ef_dora`, `em_alex`,
`em_santa` (Spanish). English has 28. **French has exactly one voice** - that asymmetry is real
and the UI must cope with it.

## What this mission builds

**#1008 speech contract → #1009 translate strings → #1010 Language tab.** In that order. #1010
ships last.

---

## The one insight the whole mission rests on

The last attempt at this shipped and was reverted (`thefrederiksen/devthrottle#2181`, 26 July).
Read `gh issue view 547 --repo thefrederiksen/devthrottle_internal` before touching anything.

It failed because **choosing a language switched the speech MODEL** to a multilingual engine that
could not speak real narration lengths - French returned silence at 155 characters, Spanish blew a
60-second deadline at 208, and the wingman writes ~500.

**This mission never switches engines.** French and Spanish are VOICES inside the same Kokoro model
already serving English. Measured 2026-07-29, ~500-char narration, warm: English 1.29s, French
1.31s, Spanish 1.24s, zero near-silent returns across 45 calls up to 800 chars.

**If anyone proposes switching the speech model based on language, that is the reverted failure
returning. Do not do it.**

---

## Phase 1 - #1008: the speech contract

The blocker, and the reason the last attempt died. The product speaks from ~10 places:

- **Four ask a model:** `WingmanTranslator` (turn narration), `AskDirectAsync` (direct reply),
  `AskAboutDevThrottleAsync` (in-product help), `CarModeBrain` (Car Mode).
- **Six or more emit hardcoded English:** `BlockedMenuSpoken`, `BlockedUnreadableSpoken`,
  `CarModeHelp.Script`, Car Mode done/cancel/give-up phrases, `BuildMenuSpoken`.

Last time **the language reached one generator out of four**, so an account set to another language
got translated narration and was answered in English the moment it was spoken to. That is what got
reported three times.

Also broken today: the same no-markdown rule is written four different ways
(`WingmanTranslator.cs:417` and `:607`, `CarModeBrain.cs:583` and `:677`), and `CleanupForSpeech` is
applied by three of the four generators but **not** by Car Mode.

**The bar:** adding a FIFTH spoken path in future must pick up the language automatically. If a
developer can add a spoken path and forget the language, this phase is not done. One shared
contract; the rule stated once; a test that fails if any spoken path ignores the configured
language.

## Phase 2 - #1009: translate the fixed strings

Owner decision: **everything speaks the language, no exceptions.** No English fragments in a French
or Spanish session.

`BuildMenuSpoken` **cannot be translated as written** - it composes narration in code from English
fragments. Restructure it to select whole translated sentences. Word order and agreement differ per
language; sentence assembly in code is not translatable.

Owner decision: **machine translation, machine review, no native human reviewer.** One model
translates, a second separately-prompted model reviews. This is an ACCEPTED RISK, recorded
deliberately - nobody in-house reads French or Spanish. Do not spend the mission trying to get a
human reviewer; it was considered and declined.

## Phase 3 - #1010: the Language tab

Replaces `AI` in the strip. English (default), French, Spanish. Shared by Cockpit and mobile
(`packages/client-core/src/settings/`).

Decided behaviour - do not redesign these:

- **Voice dropdown filtered by language, remembered PER LANGUAGE.** Store e.g.
  `{ en: bm_george, fr: ff_siwis, es: ef_dora }`. This removes restore logic entirely: nothing is
  ever overwritten, so nothing needs restoring. English → French → English gets George back.
- **Control stays visible even in French** (one voice). A control that vanishes between languages
  reads as a glitch.
- **Scope: per account**, matching the existing `ACCOUNT_SCOPE` on the AI and Car Mode tabs.
- **Sample text per language.** Auditioning a French voice on English words tests the wrong thing.
- **Dictation needs nothing** - transcription sends no language field and the provider detects it.
  French and Spanish dictation already work. Put a line on the screen saying so, so nobody hunts for
  a setting that should not exist.

Screen shape:

```
Language                             [your account]

DevThrottle talks back to you out loud - in voice
mode on your phone, and in the Cockpit. Choose the
language it speaks.

  ( ) English      (*) French       ( ) Spanish
        Default          Francais         Espanol

  Voice   [ Siwis - French, neutral            v ]

  [ Play sample ]   << Bonjour, je suis votre
                       wingman DevThrottle. >>
  --------------------------------------------------
  Typing and dictation are unaffected. Dictation
  already understands all three languages on its
  own - there is nothing to set.
```

---

## Verification

**The owner runs the live end-to-end test himself** - phone, Cockpit, real account. Do NOT build a
proof rig, do NOT ask him for accounts or devices, do NOT spend the mission on manual capture.

Your job is that the code is correct and the suite proves it. Use the **speech lab** at
`/admin/speech-lab` on devthrottle.com to sanity-check synthesis. Run `dotnet test cc-director.sln`
- architecture tests pin call sites by exact source text - and the web workspaces.

## Standing constraints

- **This worktree only.** Never the shared checkout at `D:\ReposFred\devthrottle` - other sessions
  live there.
- Commit and push to `mission/multilingual` freely. **Only the Architect lands anything on main.**
  Do not open a pull request to main; do not merge.
- **No attribution anywhere** - no `Co-Authored-By`, no "Generated with", no mention of any AI
  assistant or vendor, in commits, PRs, issues, comments or code. Check before every commit.
- Read `CLAUDE.md` in the repo root.
- Report to the Architect (`6c69987b`) when a phase is done and pushed. **Fleet messages truncate at
  the first newline** - write anything long to a file and reply with one line.
