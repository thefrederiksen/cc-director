# The owner challenged the 67 per cent, and he was right to

**Written:** 2026-09-05 by the Architect, after the owner said "I don't think the 67 per cent spoken
is correct, I didn't type that much - are they not coming from agents calling other agents? Can you
prove the 67 per cent?"

**Short answer: no, the 67 per cent cannot be defended as the truth. It is a FLOOR.** Two separate
leaks put his own words in the typed column, both measured below, and both already fixed on this
branch and not yet deployed. The figure a corrected, DEPLOYED Gateway should show him is about
**70 per cent**, and on a looser reading as high as 75.

All figures: tenant `9f19679f-...`, 29 August to 5 September 2026, the hosted Gateway's own ledger.

## 1. He was right that agent traffic reaches his count. Proved directly.

The Architect sent operator prompts into four builder sessions during this mission. Those exact
sessions were then read back out of the ledger:

```
SessionId (mine)   InputOrigin      SendSource   Cause          count
7d0d4251...        typed/unknown    UserInput    owner-submit       1
27fc2bd2...        typed/unknown    UserInput    owner-submit       1
865136e7...        typed/unknown    UserInput    owner-submit       1
ac432182...        typed/unknown    UserInput    owner-submit       1
```

An agent's prompt, carrying a HUMAN origin, counted in his typed turns. This is not a hypothesis
about a code path - it is this mission's own traffic, found in his own numbers. The whole
`typed/unknown` class is 25 turns of the week's 725.

**Already fixed on this branch, not deployed:** `GatewayEndpoints.cs:2827`,
`req.AgentDriven = callingSessionForAttribution is not null` - a session key is an agent. Inspection
finding I2-02, whose severity this confirms with live data rather than reasoning.

## 2. But it is NOT leaking in bulk. A physical test, with a control that fires.

A person cannot type into two terminals at the same moment. Counting pairs of typed turns in
DIFFERENT sessions within two seconds of each other:

| population | turns | clashes | per 100 turns |
|---|---:|---:|---:|
| counted as his - typed only | 725 | **0** | **0.0** |
| counted as his - voice and typed | 2,173 | 9 | 0.4 |
| EXCLUDED, no origin - where agent traffic lives (control) | 1,956 | 61 | 3.1 |
| EXCLUDED, framework (control) | 361 | 6 | 1.7 |

The zero is meaningful because the same instrument fires at 3.1 per hundred on the known-agent
population in the same tenant and the same week. His counted turns behave like one pair of hands;
the excluded population behaves like many actors at once. A zero from an instrument never shown to
fire would have proved nothing.

Removing the contaminated class moves the figure the OTHER way: 1,448 of 2,148 = **67.4 per cent**.

## 3. The larger effect: some of his typing IS his speech.

An independent table - `dictation_transcripts`, nothing to do with the ledger - holds **1,802**
transcriptions in the window against only **1,448** turns counted as spoken. 354 things he said did
not become spoken turns.

Testing each turn for a recorded transcription in the seconds before it, with real voice turns as the
control:

| within | typed turns matched | voice turns matched (control) |
|---|---:|---:|
| 10 seconds | **55 of 725 (8%)** | 1,411 of 1,448 (**97%**) |
| 30 seconds | 163 of 725 (22%) | 1,423 (98%) |
| 60 seconds | 247 of 725 (34%) | 1,432 (99%) |

The control at 97 per cent establishes the instrument identifies dictated turns. A crude chance
baseline from transcription density is about 3 per cent at ten seconds, so the 8 per cent is a real
excess, not coincidence - though his work is bursty rather than uniform, so the wider windows overstate.

**Taking the tight ten-second window: (1,448 + 55) / 2,148 = 70.0 per cent.** At thirty seconds it is
75 per cent. The honest statement is *about 70, and not below 67.4*.

**Already fixed on this branch, not deployed:** ruling R10 - a dictated sentence submitted through the
ordinary prompt door was recorded as typed, and now carries a spent, single-use spoken claim.

## What the final report must say, and must not

- **Must not** present 67 per cent as "the corrected figure". It is the figure under TODAY'S DEPLOYED
  attribution, computed by the corrected library. Two fixes on this branch raise it.
- **Must** say that the owner disputed it, that he was right, and what the measurement found. He
  reached the correct answer from the feel of his own working day, against two numbers and an
  Architect who had already told him his instinct was wrong once.
- The earlier finding stands unchanged: his week-of-24-August figure really was 57 per cent, and the
  92 really was wrong. Both things are true.
