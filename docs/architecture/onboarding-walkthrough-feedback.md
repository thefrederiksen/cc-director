# Onboarding wizard - walkthrough feedback

Soren's screen-by-screen notes from driving the test build (slot 5) on 2026-07-29.
Collected first, implemented at the end. Nothing here is built yet.

---

## Screen 1 - Welcome

**Screenshot:** `Screenshot 2026-07-29 074358.png`

### W-1. Delete the founder quote card

The grey card holding "I built DevThrottle because running agents was never the hard
part...", the "- Soren, founder" attribution and the "Read more" expander comes out
entirely.

> "I think this quote from me up front is stupid. People just want their shit installed,
> they don't want me talking to them about all this. So we should only explain why we're
> going through this wizard, not give stupid quotes."

**What the screen should do instead:** explain only WHY we are going through this wizard -
what it is about to set up and what the user gets at the end of it. No founder voice, no
personal message, no expandable essay.

**Code:** `FirstRunWizardDialog.axaml:150-167` (the whole `Border`), plus
`FounderMoreText` / `FounderMoreLink` and the `BtnFounderMore_Click` handler in
`FirstRunWizardDialog.axaml.cs`.

**Open question for the rewrite:** the current subtitle already half does this job
("You are about three minutes from running your first coding agent... your agents, your
code, your morning report"). With the card gone, that subtitle probably becomes the whole
screen and may want to be a short list of what the wizard covers rather than a paragraph.

---

## Screen 3 - Tools

**Screenshot:** `Screenshot 2026-07-29 074559.png` (all 9 tools ready, green state)

### T-1. NO CHANGE in the wizard. Deferred to a separate review.

Soren raised whether there should be a way to decline the tools rather than have them
installed for you. His own answer: if that choice exists it belongs in **Settings**, not
in onboarding.

> "I'm wondering if we should have a way to not install the tools, but I think it should
> be under the settings. We are going to do a different section... the tools section needs
> to be reviewed. So for now, we keep the onboarding wizard this way."

**Decision:** the Tools step stays exactly as it is for this piece of work. The opt-out
question is a separate section of work covering the tools area as a whole.

**Action outside this work:** Soren is informing the release manager himself that the
tools section needs review. Nothing for me to file or send.

**Still in scope here** (unchanged from the review, already built in the current test
build): the three real row states, the "Fix this now" repair, and the wait-or-continue
sentence. Those are about telling the truth when a tool is broken, not about whether the
user gets a choice - a different question from the one deferred above.

---

## Screen 4 - Code

**Screenshot:** `Screenshot 2026-07-29 075107.png` (four folders found, each with an Add
button, none added)

### C-1. Invert the model: add every found folder automatically, offer Remove

> "I think these should be remove instead of add. I didn't see the adds buttons, so I
> didn't add them. We should get them automatically added because then we can do all kinds
> of recommendations on them."

Every folder the scan finds is registered as it is found. The row's action becomes
**Remove**, not Add. Opting out is the deliberate act; opting in is not.

**The screenshot is the evidence.** Four folders were found - `C:\Repos` (12
repositories), `C:\ReposBPMN` (1), `C:\ReposFred` (3), `D:\Dev` (1) - and Soren walked
straight past all four without registering any of them. The Add buttons are there, right
of each row, and he did not see them. An opt-in that the product's own author misses is
not a working opt-in.

**Why auto-add, beyond convenience:** folders we know about are folders we can make
recommendations on. A folder the user never got round to adding is invisible to every
downstream feature.

**Code:** `CodeRow` at `FirstRunWizardDialog.axaml.cs:781-832` builds the Add button;
`AddCodeRootAsync` at `:850` does the registration; `SwapCodeRowActionToAdded` at `:883`
swaps in the Added pill. The scan itself streams suggestions in at `:748-755`.

### C-2. Must work on Mac as well as Windows

> "And we need to make sure this works both for Mac and Windows."

**Current state, checked:** `CodeFolderScout.CandidateRoots()`
(`src/CcDirector.Core/Onboarding/CodeFolderScout.cs:46-79`) does handle both, but NOT
equally:

- Both platforms probe the home directory - `Projects`, `Code`, `repos`, `git`, `dev`,
  `src` - plus `source\repos` on Windows and `~/Developer` on Mac.
- **The drive sweep is Windows only** (`:62-77`): it lists the top level of every fixed
  drive and takes any folder whose name looks like code. That is exactly what found all
  four folders in the screenshot - `C:\Repos`, `C:\ReposBPMN`, `C:\ReposFred`, `D:\Dev`
  are none of them under the home directory.

So a Mac user with code outside their home directory gets nothing equivalent. Needs a Mac
arm - `/Volumes/*` and the common Mac locations - or the same screen will find far less on
a Mac than it does here.

### Consequences to settle when this is built

1. **When does the write happen?** The wizard's stated principle is that doing nothing
   writes nothing (`FirstRunWizardDialog.axaml.cs:196-200`). Auto-add inverts that for
   this step. Recommended: persist at the moment the row appears, because that is what the
   row will claim - a row reading "Added" that is not yet saved is a lie if the user closes
   the window.
2. **This makes the New Session list question urgent.** Auto-adding all four folders on
   this machine registers 17 repositories in one pass. Combined with fixing #973 (folders
   added here never reach New Session), the recently-used list will need an answer for the
   long tail. Soren's earlier steer was a display-time union rather than flooding the
   recency list; that decision now has to be made rather than deferred.
3. **The time-boxed scan interacts with this.** The sweep stops after 10 seconds, so
   auto-add registers whatever was found by then. Folders found later, via "Keep looking",
   get registered when they appear.

---

## NEW screen - Browsers

**Screenshot:** `Screenshot 2026-07-29 075210.png` (the Done receipt, Browsers row reading
"Later")

### B-1. Add a Browsers step to the wizard. Not to the installer.

> "If we want people to use browsers, we need a screen in here that sets it up. And that
> means the browser harness is required. One screen could be how we deal with browsers and
> they could opt in for that. And then we set up the browsers for them and install the
> browser harness... I don't think we should go back to the installer and prerequisites in.
> I think it should be a page saying here's our preferred method of dealing with browser
> integration and it's a tool called browser harness. You want to install that and set up
> some browser profiles that can have different logins."

### What already exists (checked, not assumed)

Most of this is built. The new step is largely wiring, plus one genuinely new capability.

| Piece | State today | Where |
|---|---|---|
| Per-browser isolated profile with its own login | BUILT | `AutomationBrowserService.Create` - own `--user-data-dir`, own allocated debug port |
| Human signs a browser in, once | BUILT | `BrowserSignInFlow.cs` |
| Browsers list, status, launch/stop | BUILT | `AutomationBrowserService`, `BrowsersRailGroup`, `BrowserSettingsView` |
| Agent attaches via the harness | BUILT | `BU_NAME` / `BU_CDP_URL` handed out by `AutomationBrowserViewFold` |
| Command-line surface | BUILT | `BrowserEndpoints.cs`, `cc-devthrottle browser` |
| **Installing browser-harness** | **DOES NOT EXIST** | `IsHarnessInstalled()` only checks PATH; the UI links out to `HarnessInstallUrl` on GitHub |

The design constraint that already shipped is worth repeating because it is exactly the
"different logins" Soren described: Chrome 136+ refuses remote debugging on the default
profile, and App-Bound Encryption refuses to carry a login into a copied profile - so each
drivable browser is its own folder, signed in ONCE by a human.

### Recommendation

**1. One opt-in screen, after Screenshots and before Gateway.** It belongs beside
"SHOW, DON'T TYPE" - both screens are about how an agent gets context from you. Gateway
stays the last real step before Done.

**2. Make the case FOR setting it up now, with an honest way out.** (Soren's steer, which
overrides my first recommendation to default to skipping.)

> "I think we should try to sign up the browser, saying if you set up your browsers now, it
> will be much easier to use, but you can also set up browsers later if you prefer to get
> the installation going."

So the screen actively recommends doing it now and says why - it is easier here than
wiring it up afterwards - while stating plainly that later is a supported choice for anyone
who just wants to finish installing. Concretely:

- Primary action: set the browsers up now.
- Quiet secondary: set them up later, with the rail named as where that happens.
- The copy carries the trade, in the user's terms: doing it now is less work; doing it
  later is fine and costs nothing but a return trip.

Note this is deliberately NOT the same as the Code step's auto-add. Nothing is installed
without the user choosing it, because it is third-party software - but the screen does take
a side rather than sitting neutral.

**3. Name the third-party tool on screen.** browser-harness is from browser-use, not from
us. A wizard that silently installs someone else's software on a user's machine is not
acceptable; the screen must say what it is installing and from where before the user opts
in.

**4. Take them through ONE browser here, not several.** Signing in is a human act,
interactive, one browser at a time - three signed-in profiles cannot be batched into a
three-minute wizard, and trying would turn the recommended path into the slow one. So the
recommended path stays short and finishes:

  - explain what this gives them (an agent that can drive a real signed-in browser)
  - accept -> install browser-harness, streaming progress, reporting failure on screen
  - create ONE browser profile and take them through signing it in, here and now
  - say plainly that more profiles are added from the Browsers group in the left rail

The point of recommending "now" is that the user ends the wizard with a working, signed-in
browser. If the step installs the harness but leaves them with nothing signed in, the
recommendation was hollow - so the sign-in is part of the recommended path, not an extra
after it.

**5. The Done receipt row stops being a pointer.** Today it always reads "Later" whatever
the machine's state is - it is advertising copy, not a status. With a real step it reports
what actually happened: harness installed, one browser created, signed in or not.

**6. Do not reopen the installer.** Agreed, and there is a second reason beyond Soren's:
the installer is finished and proven on a clean machine, and making a third-party Python
tool a prerequisite would gate every DevThrottle install on it - including the majority who
never drive a browser.

### Open questions to settle before building

- **What does installing browser-harness actually require?** It is a Python tool from
  browser-use. Whether that is pip, uv, or a bundled runtime decides whether this is a
  small step or a large one, and whether it can be done without leaving the wizard. NOT yet
  checked - this is the one real unknown in the whole step.
- **Mac and Windows parity**, same as the Code step. The install path is likely to differ.
- **What happens when the install fails?** Per the no-fallback rule it must say so and
  offer the manual install page - never silently continue as if it worked.

---
