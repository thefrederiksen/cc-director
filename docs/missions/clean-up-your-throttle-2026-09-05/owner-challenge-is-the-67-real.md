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

---

## 4. The open question, now measured: do the keystroke turns actually compose anything?

The Architect left one gap open when answering the owner: a turn is counted when a submission carries
Enter, so pressing Enter to accept a prompt or pick a menu option counts as a typed turn even though
nothing was composed. Phase one had not separated that from records simply being lost.

Measured over 2026-W35, taking every terminal-keystroke turn (`typed/desktop`, no send source) and
asking whether a USER record with text appeared in that session's transcript within the harness's own
23-second join window - and, when none did, whether that session was being ingested at all:

| | turns | share |
|---|---:|---:|
| produced a real prompt with text in the transcript | 482 | 81.6% |
| session WAS being ingested and no prompt appeared - **composed nothing** | 40 | 6.8% |
| session not ingested at all - cannot be told either way | 69 | 11.7% |

The middle row is the honest measure of bare keystrokes, because the session was demonstrably being
recorded at the time and still no prompt materialised. Removing those 40 moves that week from
**56.8 to 58.1 per cent**. Treating every one of the 109 unexplained as a bare keystroke - the most
generous reading available - reaches 60.5 per cent.

**Applied to the owner's last seven days** at the same 6.8 per cent rate, roughly 36 of the 535
keystroke turns composed nothing, giving `(1,448 + 55) / (2,148 - 36)` = **about 71 per cent**.

## The final answer to the owner's question

**About 70 to 71 per cent, and under the most generous reading of every remaining uncertainty, the low
to mid seventies. Not 80, and not 90.**

He was right about the direction three separate times - agent traffic does reach his count, his
dictation is being filed as typing, and some of his typed turns compose nothing - and each correction
moved the figure his way. They are worth roughly one, three, and one and a half points. Together they
do not reach his estimate, and the reason is simple and checkable: about 700 of the week's typed turns
came from his own keyboard and his own phone through the only four paths in the product that can stamp
a typed turn as a person's, and 82 per cent of the keystroke ones left a real prompt, with text, in the
agent's own transcript.
