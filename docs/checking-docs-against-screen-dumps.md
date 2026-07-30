# Checking a document against what the product actually says

**When you write a sentence about what a screen says, check it against the committed accessibility
dump for that screen — not against the XAML.**

The XAML tells you what a screen *can* show. The dump tells you what it *did* show, on a real machine,
in a real state. Those are different facts, and the gap between them is where wrong documentation
comes from.

This is a repository capability, not a favour from whoever happens to be running a virtual machine.
The dumps are committed. Anyone can read them, with no machine, no walk, and nobody to ask.

---

## Where the dumps are

They are in the **`devthrottle_internal`** repository — not this one:

```
docs/qa/runs/<date>-<tag>/report.html
```

The clean-machine install and wizard run, for example, is
`docs/qa/runs/2026-07-30-INSWIZ/`. Its `report.html` carries the full accessibility text of every
screen the walker visited: all three installer screens and all eight setup wizard steps. Alongside it,
`run-notes.md` records what the run found and what it could not verify, and `shots/` holds the
screenshots.

Read it straight from GitHub:

```bash
gh api repos/thefrederiksen/devthrottle_internal/contents/docs/qa/runs/2026-07-30-INSWIZ/report.html \
  --jq '.content' | base64 -d > report.html
```

The documents being checked live in this repository; the dumps live in the internal one. Look in the
wrong repository and you will conclude the evidence does not exist.

## The split is load-bearing, so know which side you are on

Nothing else states this plainly, and it has caused the same mistake three times in a single task:

| | Lives in |
|---|---|
| Product code, and the public documents in `docs/public/` | **`devthrottle`** (this repository) |
| Issues, design notes, the website, and the QA runs with their dumps | **`devthrottle_internal`** |

So an issue in `devthrottle_internal` will routinely name a file that is in `devthrottle`, and the
evidence for a sentence in `devthrottle` will routinely be in `devthrottle_internal`.

**An absent result is only evidence once you have established that the search could have found the
thing.** A grep that returns nothing because you are in the wrong repository looks *exactly* like a
grep that returns nothing because the claim is false. Same empty output, opposite conclusions. Before
you report an absence as a finding, prove the search had reach: run it against something you know is
there, or name the repository, path and revision you searched.

This is the same failure as a truncated list read as "not running", or a `head -3` that hides the
fourth match. The tool answered a smaller question than the one you asked, and the answer looked
identical.

## How to use it

1. List every sentence in your page that asserts what a screen **says**, **shows**, or **offers**.
2. Find that screen in the dump and compare, word for word.
3. Quote the product where the product says it better than you do. It usually does.
4. For anything the dump cannot confirm, decide deliberately — see below.

## Three failure modes this catches

**Wording drift.** A label changes, the dump changes with it, and no screenshot catches it because the
picture still looks plausible. This is the one nothing else finds.

**Writing for the wrong machine.** A page describes the state *your* machine is in, not a new one.
A getting-started page has exactly one reader — someone on a clean machine — and the dump from a clean
run is the only artifact that shows what that reader sees. The Screenshots step guesses your
screenshots folder on a machine that has one, and reports it cannot find one on a machine that does
not. Describing only the first is writing for yourself.

**Repeating a product falsehood.** If a screen asserts something untrue, a document that faithfully
quotes it now asserts it too, with the documentation's authority added. Check the claim, not just the
wording — and if a screen is known to be wrong about something, say so rather than repeating it.

## Evidence you have, in order

Not all support is equal, and the difference is worth stating in review rather than blurring:

| Strength | What it is |
|---|---|
| **Witnessed** | The dump shows the screen saying it, on a real machine in a known state. |
| **Source** | The code plainly builds it. Real evidence, weaker: it shows what *can* happen, not what did. |
| **Neither** | Inherited from an older document, or assumed. This is not evidence at all. |

**No evidence and weaker evidence are not the same thing**, and neither is a reason to stay silent.
They are a reason to decide, per claim, and to say which you had:

- **Cut it** when it is unwitnessed *and* sits next to something known to be wrong.
- **Move it** when it is true but not visible on the screen you were describing — say that it happens,
  not that the screen shows it.
- **Keep it**, trimmed to what the source actually shows and using the real on-screen labels, when the
  code plainly builds it and it was unwitnessed only because the run took a different path.

Writing about behaviour nobody has ever witnessed is how documentation goes wrong. The rule is not
"never write it" — it is "never write it by accident".

## Why this exists

A cross-check of the install and setup wizard pages against one clean-machine run found nine defects
in text that had already been ground-truthed against source. Five of them were invisible to source
reading, because the code was right and the machine was in a state the author had not pictured.

The sharpest was not in the wizard at all: both getting-started pages told a brand-new user to confirm
their install by running a `cc-*` tool that fails on a clean install. The first command handed to a
stranger was the one most likely to fail, in the minute after installing, and it would have read as a
broken install.

No screenshot would have caught it. The dump did.
