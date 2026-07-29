# Multilingual mission - Architect rulings

Decisions made mid-mission. Durable, so a re-seated Manager does not re-ask.

---

## Ruling 1 - Accented characters in spoken content: ALLOWED, and required

**Asked by the Manager, Phase 2 (#1009).** Correct French and Spanish need accents
(`Désolé`, `¿Qué pasó?`), which appears to collide with the repo-wide ASCII-only output rule.

**Ruling: spoken CONTENT carries correct accents. Everything else stays ASCII.**

This is not a relaxation of the rule; it is the rule applied to the right target.

### Why it is not merely a style preference

Kokoro phonemizes the text it is given. `Desole` and `Désolé` do not phonemize the same way -
`e` and `é` are different vowels, and the model will pronounce the stripped form wrong. Stripping
accents does not produce slightly-off French; it produces a voice mispronouncing words.

Every measurement this mission rests on used properly accented text. The ~500-character French
narration that returned in 1.31 s with a 0.037 word error rate was accented. An unaccented corpus
was never tested and there is no reason to believe it scores the same.

So stripping accents would break the feature the mission exists to deliver, and would do it
silently - the audio would still play.

### What the ASCII rule is actually protecting

Its own stated reason is Windows terminals, log files and encoding errors. That is about **output
channels**, not about **payload data**. Spoken strings are data: they live in resource files, travel
over HTTP as UTF-8 to a synthesis API, and are never meant to be printed to a console. The rule's
purpose is untouched by letting them carry accents.

### The boundary, precisely

**Accents ALLOWED - spoken content only:**
- The translated strings themselves, in whatever resource file holds them
- The text sent to the TTS endpoint
- Sample text shown on the Language tab
- Test fixtures asserting on that content

**ASCII still binds everything else, unchanged:**
- Identifiers, class and method names, resource KEYS
- Code comments and documentation
- Log and console messages, error text, debug output
- Commit messages, pull request text, issue text
- Test NAMES (the content they assert on may be accented)

### Two guards this ruling requires

1. **Never write spoken content raw to a log or console.** If a log line must reference a spoken
   string, log its resource key, its length, or a hash - never the text. This is what keeps the
   original rule's purpose intact rather than merely arguing around it.
2. **Pin the encoding with a test.** A resource file read as cp1252 instead of UTF-8 turns `é` into
   mojibake, and it fails silently - the audio just comes out wrong. There must be a test that reads
   a known accented string through the real loading path and asserts the characters survive
   byte-for-byte. This is the exact class of bug that hides until a customer hears it.

The second guard matters more than it looks. On this machine the default encoding is cp1252, and
several tools in this repo have already been bitten by it.
