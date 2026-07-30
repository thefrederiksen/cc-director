<!--
  SCREENSHOTS ARE MISSING FROM THIS PAGE. Tracked: thefrederiksen/devthrottle_internal#1053.

  Sixteen images were deleted in thefrederiksen/devthrottle#2287 because every one showed a screen
  that no longer exists (the Workstation/Gateway role picker, the Prerequisites screen, the Gateway
  role). The text was rewritten against the shipping wizard; the pictures were not recaptured,
  because the capture belongs to the clean-machine QA run rather than to a separate walk.

  If you are adding them, read #1053 first. Three things matter more than the images:

    1. Caption every image with its step number from FirstRunWizardModel.CanonicalOrder.
       Gateway is step 2, NOT 7. Browsers is step 7, NOT 6. Three briefs got that wrong before the
       prose was fixed, and a caption is where the error comes back.
    2. The Agents step needs the ZERO-AGENT frame. A found-state picture under this page's
       zero-state prose is the same failure that got the original sixteen deleted.
    3. Capture at a roomy resolution. At 1024x768 the Welcome and Done steps clip (#1046), and a
       screenshot taken where a defect does not appear must not become the reason that defect is
       closed.
-->

# Setup Wizard Walkthrough

Getting DevThrottle running means going through **two** short wizards, and it is worth knowing
which is which:

| | Where it runs | What it is for |
|---|---|---|
| **[The installer](#part-1--the-installer-three-screens)** | **DevThrottle Setup**, before the app exists | Puts the files on the machine. Three screens, no decisions. |
| **[The setup wizard](#part-2--the-setup-wizard-eight-steps)** | Inside the Director, the first time you open it | Sets this machine up: your gateway, your agents, your code. Eight steps. |

The split matters, because it is where the account lives. **The installer never asks who you are.**
The Director installs and runs with no account at all. The one sign-in in DevThrottle is on the
*second* screen of the *second* wizard, it is about connecting a gateway, and **"Not now" is a real
answer**.

> Prefer no window at all? The [Installation](02-installation.md) page covers the command line
> installer and the one-prompt install. Both front-ends share a single install engine and produce
> exactly the same per-user layout under `%LOCALAPPDATA%\cc-director`.

---

## Part 1 -- The installer (three screens)

### Getting it

Download **DevThrottle Setup** from [devthrottle.com/download](https://devthrottle.com/download).
There is no sign-up in front of the download.

If you would rather verify the file yourself, take it from the
[latest release](https://github.com/thefrederiksen/devthrottle/releases/latest) instead --
`devthrottle-setup-win-x64.exe` plus `release-manifest.json` -- and check the hash before running it:

```powershell
$expected = (Get-Content release-manifest.json -Raw | ConvertFrom-Json).assets.'devthrottle-setup-win-x64.exe'.sha256
$actual   = (Get-FileHash devthrottle-setup-win-x64.exe -Algorithm SHA256).Hash.ToLower()
if ($actual -eq $expected.ToLower()) { "MATCH - safe to run" } else { "MISMATCH - do not run" }
```

The Windows binaries are code-signed as Center Consulting Inc. No administrator rights are needed at
any point.

### The three screens

| Step | What happens |
|------|--------------|
| **1. Welcome** | Says what is about to be installed. On a machine that already has DevThrottle, this becomes the Update screen and is also where an uninstall starts. |
| **2. Install** | Downloads and places each component, verifying every file against the manifest's SHA-256. |
| **3. Complete** | Confirms what was installed and offers to open the app. |

That is the whole thing, and it is the same path for every install and every update.

**There is nothing to choose.** You are not asked to pick an install type, you are not asked for a
key, and you are not asked to sign in. The Welcome screen says so in as many words -- *"No account is
needed to install it"*. The installer lays down the Director, every `cc-*` command line tool, and the
Launcher, and stops.

> **This used to be different.** Older versions of this page walked you through a **Workstation or
> Gateway** role picker, a **Prerequisites** screen, and a **Skills** screen. All three are gone:
>
> - The **role picker** is gone. Every machine gets the same install. Connecting a gateway is a
>   later, optional step done from the app.
> - The **Prerequisites** screen is gone. It existed for the one row that could actually block you --
>   the .NET runtime. Every Windows binary now carries its own runtime, so nothing needs to be on the
>   machine first.
> - The **Skills** screen is gone. Skills are held centrally and fetched by whichever agent uses one;
>   the installer places none.
>
> If you are following an older guide that tells you to run `setx OPENAI_API_KEY` before installing,
> stop -- that requirement was removed. DevThrottle has no bring-your-own-key.

### Updating or uninstalling

If DevThrottle is already on the machine, the wizard opens in **Update** mode. The Welcome screen
shows the installed version, the version available, and -- read only -- the install type it detected
from disk. It does not ask you to choose one. **Next** updates only the components that are behind.

The same screen is where **Uninstall DevThrottle** starts. The confirm screen lists exactly what will
be removed and, just as importantly, what is **kept**: your configuration, vault secrets, signed-in
browser sessions and recordings all stay under `%LOCALAPPDATA%\cc-director` unless you tick **Also
delete my data**.

Day to day you will not see this wizard again. Updates are silent and automatic -- no banner, no
prompt, no administrator rights.

---

## Part 2 -- The setup wizard (eight steps)

The first time you open the Director it walks you through eight steps. Every one of them is about
**this machine**, and every one can be skipped.

The wizard tells you where you are: beside the progress dots it reads **"Step N of 8"** followed by
the step's name. If your screen disagrees with the numbering below, trust your screen and please say
so -- the page is written against the shipping order, and that order has been got wrong before.

| # | Step | The question it answers |
|---|------|-------------------------|
| 1 | **Welcome** | What the next few minutes are for. |
| 2 | **Your gateway** | Do you want your agents on your phone? *This is the only step that involves an account.* |
| 3 | **Your agents** | Which coding agents are on this machine? |
| 4 | **Tools** | The `cc-*` toolbelt, installing itself. |
| 5 | **Your code** | Where do your repositories live? |
| 6 | **Screenshots** | Which folder do your screenshots land in? |
| 7 | **Browsers** | Should your agents be able to drive a real browser? |
| 8 | **Done** | A receipt of what was set up. |

### 1. Welcome

Lists what the wizard is about to cover, one row per step, so you know what the next few minutes
are for. Two ways on: **Set me up**, or **Skip setup and figure it out myself**.

### 2. Your gateway

The gateway comes second on purpose. Connecting is *who you are*; everything after it configures one
particular computer. It is also the step that carries the real payoff, so it sits where it will
actually be seen rather than five screens in.

> The gateway is what lets you check on your agents from your phone, use voice, and get your morning
> report.

Three cards:

| Card | What it means |
|---|---|
| **Hosted gateway** *(recommended, pre-selected)* | We run it. Sign in and this machine is enrolled -- phone access, voice and the morning report work immediately. Part of **Pro**; see [pricing](https://devthrottle.com/pricing). |
| **Self-hosted gateway** | Run your own gateway and join it. For advanced setups. |
| **Not now** | Local-only on this machine. Connect any time from Settings. |

Choosing hosted or self-hosted opens your browser to sign in with **Google, GitHub, or email**, and
the wizard continues by itself once you are done. When it lands, the step shows a green **Connected**
badge and the gateway it enrolled this machine with. If the sign-in fails, the step says why in plain
English and offers **Try again** -- it never carries on as though it had worked.

**This is not a gate.** "Not now" is as easy to click as the other two, and everything else in
DevThrottle works without it. A first screen that read as sign-up-to-continue would change what the
product is.

### 3. Your agents

Scans the machine for coding agents -- Claude Code, Codex, Gemini, Copilot, Cursor, Grok, opencode,
Pi -- and lists what it found.

This is the one step that will not let you sail past a genuine problem. On a clean machine it will
find none, and it says so plainly -- *"You need a coding agent"* -- explains that DevThrottle runs and
supervises command line coding agents and did not find any here, lists the eight it knows, and gives
you three ways forward:

| Button | What happens |
|---|---|
| **Install Claude Code for me** | Installs it right there. Takes about a minute; you sign in to Claude the first time an agent starts. |
| **Re-check** | Scans again, for when you have just installed one yourself. |
| **I'll do this later** | Carries the job forward as a to-do rather than pretending it is done. |

### 4. Tools

The `cc-*` command line tools, installing themselves while you watch. You never run an updater.

If a tool is still missing after the wizard has waited a reasonable time, it says so and offers
**Fix this now** -- the same repair the Home screen runs. It will not leave an "Installing..." label
spinning forever on something that is never going to finish.

### 5. Your code

Sweeps the usual places for git repositories and adds what it finds, so DevThrottle can offer them
when you start an agent. Remove any you would rather it left alone.

### 6. Screenshots

Snap a screen, drag the image straight onto an agent -- the fastest way to show it exactly what you
mean. DevThrottle watches one folder for new screenshots.

It guesses the folder, tells you where the guess came from, and shows you your most recent images as
proof it picked the right one. Not sure? **Take a screenshot and we'll find it** -- press your normal
screenshot shortcut and the wizard watches where the file lands.

### 7. Browsers

An agent can drive a real browser that is already signed in to your accounts -- reading a dashboard,
filling in a form, checking a page for you.

This step is last of the real steps because it is the one that may ask you to sign in to something,
and that cannot be hurried. It recommends setting up now, and names the tool and who makes it before
you accept. If the install fails it says so and links the manual page; it never continues as though
it had worked. The way out -- **Set up browsers later** -- names where later happens: the Browsers
group in the left rail does the same job.

### 8. Done

A receipt of what was actually set up, then **Start my first agent** or **Take me to the board**.

> Start an agent on one of your repos -- give it a small task and watch the card, not the terminal.
> Tomorrow morning, DevThrottle reports back on how it went.

**There is no morning-report step**, and that is deliberate. The report is one person, one email,
delivered through the gateway. Asking about it here would ask the same person the same question again
on every machine, and write the answer somewhere the other machines could not see it. You set it up
once, for your account, not once per computer.

---

## After installation -- verifying

Open a **new** terminal, so the updated `PATH` is loaded, and confirm the tools are there:

```powershell
cc-pdf --version
cc-html --version
```

Or ask the installer directly:

```powershell
devthrottle-setup-cli-win-x64.exe status --json
```

The installer writes a detailed log for troubleshooting:

```
%LOCALAPPDATA%\cc-director\logs\setup\setup-<timestamp>.log
```

From here on, updates are automatic and silent -- the app keeps every component current in the
background, with no prompts and no administrator rights. See
[Installation](02-installation.md) for the details.
