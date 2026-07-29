# Installation

DevThrottle runs on Windows and macOS (Apple Silicon). There are **two ways to install it**: a graphical wizard, and a command line installer for headless machines, scripts, and AI coding agents. Both end with the same working machine, with one difference in timing - the wizard leaves the `cc-*` tools to finish setting up the first time you open the Director, while the command line installer provisions them during the install.

**Nothing has to be on the machine first.** The Director and the Launcher carry their own .NET runtime, the `cc-*` tools bring their own Python, and no inbound port is ever opened - so there is no runtime to install, no account needed to install, and no gateway to set up.

## What gets installed

Three things, and nothing else.

| | What it is |
|---|---|
| **Director** | The application. It runs your coding agents side by side and tells you when one needs you. |
| **Launcher** | A small background app that keeps the Director available and reachable. |
| **Tools** | The `cc-*` command line tools. They finish setting up the first time you open the Director. |

Everything is per-user: no administrator rights, nothing written outside your own profile.

## Install with the wizard

### Windows

Download **DevThrottle Setup** from your [account page](https://devthrottle.com/signup) and run it. Three steps - Welcome, Install, Complete - and it is done.

### macOS

Install and open the wizard with one command in Terminal:

```bash
curl -fsSL https://raw.githubusercontent.com/thefrederiksen/devthrottle/main/scripts/install-mac.sh | bash
```

The command downloads the wizard from the [latest release](https://github.com/thefrederiksen/devthrottle/releases/latest), verifies its SHA-256 hash against the release manifest, places **DevThrottle Setup** in `~/Applications`, and opens it.

It uses `curl` on purpose. The wizard is ad-hoc-signed, not notarized by Apple, so a **browser** download of it is blocked by Gatekeeper — "Apple could not verify 'DevThrottle Setup' is free of malware" — and on macOS 15 (Sequoia) and later that dialog offers no way to open the app (the old right-click -> Open bypass was removed). Downloads made with `curl` are never quarantined, so the wizard opens normally.

If you already downloaded the zip with a browser and hit that block, either run the command above (recommended), or open **System Settings -> Privacy & Security**, scroll down to the message about "DevThrottle Setup", and choose **Open Anyway**. Removing the quarantine flag with `xattr -dr com.apple.quarantine "DevThrottle Setup.app"` also works, but only on a freshly unzipped copy that has never been launched — macOS remembers a copy it has already blocked.

On macOS the Director installs to `~/Applications`, the `cc-*` tools live in one shared Python environment under `~/Library/Application Support/cc-director`, and the tools are symlinked into `~/.local/bin` (added to your shell `PATH`). Apple Silicon only.

## Install from the command line

The same install, with no window and no clicking. This is the right choice for a headless machine, a script, or an AI coding agent.

Download the command line installer from the [latest release](https://github.com/thefrederiksen/devthrottle/releases/latest) - `devthrottle-setup-cli-win-x64.exe` on Windows, `devthrottle-setup-cli-mac-arm64` on macOS - and run:

```powershell
# Windows
devthrottle-setup-cli-win-x64.exe install
```

```bash
# macOS
chmod +x devthrottle-setup-cli-mac-arm64
./devthrottle-setup-cli-mac-arm64 install
```

Useful options:

| Option | What it does |
|---|---|
| `--json` | Machine-readable output, for a script or an agent to parse |
| `--log-file <path>` | Also write the live progress to a file |
| `--dry-run` | Show what would be installed and change nothing |
| `--release-dir <dir>` | Install from a local directory instead of GitHub (offline) |

Other commands: `status` (what is installed), `update`, `uninstall`, `plan`, `rollback`, `prereqs` (is a coding agent present), `signin`, `enroll`, `autostart`. Add `--help` to any of them.

### Exit codes

These are a contract. Scripts and agents can rely on them.

| Code | Meaning |
|---|---|
| `0` | Done. For `install` and `update` this includes "everything was already current". |
| `1` | It ran and failed. The output names what failed. |
| `2` | The command line was wrong - unknown verb, missing value, bad combination. Do not retry. |
| `3` | **`prereqs` only** - there is no coding agent on this machine. `install` never returns this. |

## Install with an AI coding agent

Hand your agent this. It is the whole prompt.

```text
Install DevThrottle on this machine, unattended.

1. Download the command line installer for this platform from the latest release:
   https://github.com/thefrederiksen/devthrottle/releases/latest
   Windows: devthrottle-setup-cli-win-x64.exe
   macOS:   devthrottle-setup-cli-mac-arm64
2. Run:  <installer> install --json --log-file ./devthrottle-install.log
3. Verify:  <installer> status --json
   Every component should be present, at the version in the release.
4. Report the version installed, the install path, and the log path.

Exit codes from install: 0 done, 1 failed, 2 bad command line.
(3 comes only from the separate `prereqs` check: no coding agent on this machine.)

Do NOT install a .NET runtime - the Director and the Launcher carry their own.
Do NOT stop or kill any process that is not part of this install.
If a step needs a person - signing in, or an operating system permission prompt -
stop and say exactly what is needed. Do not work around it.
```

Signing in is the one step that needs a person: `<installer> signin` opens a browser to sign in or create a free account. To join a gateway you already run on another machine, use `<installer> enroll` instead.

## Verify the install

Open a **new** terminal - one that was already open will not have the new `PATH` - and run:

```bash
cc-excel --version
cc-hardware
```

Or ask the installer directly:

```bash
devthrottle-setup-cli-win-x64.exe status --json
```

## Recommended, not required

None of these is needed to install DevThrottle or to start it. They are worth having, and here is exactly what you lose without each.

| Tool | Without it |
|---|---|
| **A coding agent** - Claude Code, Codex, Gemini, Copilot, Cursor, Grok, OpenCode or Pi | Your board has nothing to run. DevThrottle drives all eight; any one of them is enough. |
| **Git** | The Director cannot read your repositories, so branch names and change counts stay blank. |
| **GitHub CLI** (`gh`) | The repository picker, pull request lists and merged-pull-request checks are unavailable. Sign in once with `gh auth login`. |
| **Python 3.11+** | Only your own scripts. The `cc-*` tools bring their own Python. |
| **Node.js 20+** | MCP servers and the browser tools. |

The Director checks for these when it opens and can add the coding agents it finds to your board, so you can install any of them later and nothing needs reinstalling.

### If a tool is not detected after installing it

A shell that was already open still has the old `PATH`. Open a new terminal, or ask the installer again with `prereqs`. The Director looks in the usual install locations as well as `PATH` - including `~/.local/bin` and the global npm directory - so an agent installed by its own official installer is found even when `PATH` is stale.

### Optional (for specific tools)

| Requirement | Needed for |
|-------------|------------|
| FFmpeg | cc-transcribe, cc-video |
| Graphviz | cc-docgen (C4 diagrams) |
| Playwright browsers | cc-browser, cc-reddit, cc-crawl4ai |
| OpenAI API key | cc-image, cc-voice, cc-whisper, cc-computer, cc-transcribe, cc-photos |
| Google OAuth credentials | cc-gmail |
| Azure App Registration | cc-outlook |

## Skills

DevThrottle's skills are held centrally on the Gateway, so a skill is fixed once and is live
everywhere - no release, and nothing for you to update or uninstall.

The installer itself places no skill files on your machine. DevThrottle downloads them while it runs
and keeps them current for you:

- **DevThrottle downloads your enabled skills from the Gateway** as soon as it connects, and again
  about once a minute after that.
- **It writes each one into `~/.agents/skills`** - the shared folder that Codex, Gemini, Grok, pi,
  Copilot and opencode all read on their own, with nothing to configure. Claude Code and Cursor do not
  read that folder, so each skill also gets a shortcut into their own folder pointing at the same copy.
  There is only ever one copy, so there is nothing that can drift out of step.
- **Each agent then finds the skills by itself**, through its own skills feature. There is no
  DevThrottle command in the way.
- **Switching a skill off removes it from your disk.** DevThrottle matches what is on the machine to
  what the Gateway is serving, so a skill you turn off is gone the next time a session starts - it
  cannot keep working from a leftover file.

Your own skills are untouched by all of this. A skill in your personal `~/.claude/skills/` folder, or
one in a repository's own `.claude/skills/` folder, keeps working exactly as before, and a local skill
wins if it shares a name with a central one - DevThrottle only ever touches the skills it put there
itself.

## Setting Up Email Tools

### Outlook (cc-outlook)

1. Create an Azure App Registration with Mail.Read and Mail.Send permissions
2. Configure the tool:

```bash
cc-outlook accounts add your@email.com --client-id YOUR_CLIENT_ID
cc-outlook auth
```

3. Follow the device code flow to authenticate

### Gmail (cc-gmail)

1. Create OAuth credentials in Google Cloud Console
2. Configure the tool:

```bash
cc-gmail accounts add personal --default
cc-gmail auth
```

## Setting Up Browser Automation

Install Playwright browsers (needed for cc-browser, cc-reddit):

```bash
npx playwright install chromium
```

## Environment Variables

Set the OpenAI API key for AI-powered tools:

```bash
set OPENAI_API_KEY=your-key-here
```

Or add it permanently through Windows System Properties > Environment Variables.

## Multi-Machine Setup (Remote Access)

Install DevThrottle on each machine the same way - there is no role to choose during setup. Then connect each one to your gateway:

```bash
devthrottle-setup-cli-win-x64.exe enroll            # your own gateway, discovered automatically
devthrottle-setup-cli-win-x64.exe enroll --hosted   # DevThrottle's hosted gateway
```

Every Director and Launcher dials **out** to the gateway and keeps that connection open. Nothing listens for inbound connections, so there is no port to forward, no firewall rule to add, and no mesh network required.

### Troubleshooting

If a machine does not appear in your fleet, check three things on that machine, in this order:

1. Is the Launcher running? `devthrottle-setup-cli-win-x64.exe status --json` reports it.
2. Is it enrolled? Re-run `enroll` - it is safe to repeat.
3. Can it reach the gateway? A proxy that blocks long-lived outbound connections is the usual cause.

## Next Steps

- [Quick Start](quick-start.md) -- Walk through your first session
- [Tools Overview](../tools/overview.md) -- See all available tools
