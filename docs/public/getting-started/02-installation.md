# Installation

DevThrottle runs on Windows and macOS (Apple Silicon) and requires a few prerequisites. This guide walks you through getting everything set up.

## Prerequisites

The DevThrottle **Setup** app checks for these on its Prerequisites screen. On Windows **only the .NET 10 Runtime is required** - it is what actually runs DevThrottle - and Setup can install it for you. The rest are recommended: Setup offers to install each one on the spot, and you can continue without them and add them later. On macOS nothing is required, because the macOS build carries its own runtime.

| Tool | Required? | Minimum | What you lose without it |
|------|-----------|---------|--------------------------|
| [.NET 10 Runtime](#net-10-runtime) | **Required** (Windows only) | 10.0 | DevThrottle will not start |
| [Claude Code](#claude-code) | Recommended | latest | No coding agent installed yet - but DevThrottle runs seven others |
| [Python](#python) | Recommended | 3.11+ | Your own Python scripts. The `cc-*` tools bring their own Python and do not need this |
| [Node.js](#nodejs) | Recommended | 20+ | MCP servers and the browser tools |
| [Tailscale](#tailscale-self-hosted-gateway-only) | Optional, **self-hosted gateway installs only** | latest | Reaching a self-hosted gateway's Cockpit from a phone or another computer |

On **Windows**, Setup can install every one of these for you via `winget`: each row shows an **Install automatically** action while the tool is missing. If `winget` is unavailable -- it is absent on some locked-down machines -- use the download link in the same row and click **Re-check**. On **macOS** there is no `winget`, so the links are the install path.

> **Just installed one of these and Setup still says "Not found"?** See [If a tool is not detected after installing it](#if-a-tool-is-not-detected-after-installing-it).

### .NET 10 Runtime

The Director, Gateway, and Cockpit are .NET 10 apps. They are shipped framework-dependent (small downloads), so the **ASP.NET Core Runtime 10** must be present on the machine.

- **Windows:** the Setup app detects it and offers **Install automatically** (runs `winget install Microsoft.DotNet.AspNetCore.10`). Or install it yourself from [dotnet.microsoft.com/download/dotnet/10.0](https://dotnet.microsoft.com/download/dotnet/10.0).
- **macOS:** the macOS Director app is self-contained and does **not** require a separate .NET install.

Confirm: `dotnet --list-runtimes` includes a `Microsoft.AspNetCore.App 10.x` line.

### Claude Code

The Anthropic CLI. **The easiest route on Windows is to let Setup do it** -- the Claude Code row has an **Install automatically** action that runs `winget install Anthropic.ClaudeCode` silently. To install it yourself, use the official **native installer** -- **do not use `npm`**, which is the usual cause of "`claude` command not found" and PATH problems.

- **Windows (PowerShell):** `irm https://claude.ai/install.ps1 | iex`, or `winget install Anthropic.ClaudeCode`
- **macOS / Linux:** `curl -fsSL https://claude.ai/install.sh | bash`

Then run `claude` once to sign in (requires a paid Claude plan -- Pro, Max, Team, or Enterprise).

Confirm: open a **new** terminal and run `claude --version`. More options: [Anthropic's setup guide](https://code.claude.com/docs/en/setup).

### Python

Python 3.11 or higher (used by several cc-* tools and MCP servers).

- **Windows:** download from [python.org/downloads](https://www.python.org/downloads/) and **check "Add python.exe to PATH"** in the installer, or run `winget install Python.Python.3.12`.
- **macOS:** `brew install python@3.12` (or download from python.org).

Confirm: `python --version` (on macOS, `python3 --version`) prints `Python 3.11+`.

### Node.js

Node.js 20 or higher (MCP servers and browser tools).

- **Windows:** download the LTS installer from [nodejs.org](https://nodejs.org/), or run `winget install OpenJS.NodeJS.LTS`.
- **macOS:** `brew install node` (or download from nodejs.org).

Confirm: `node --version` prints `v20+`.

### Tailscale (self-hosted gateway only)

**Most people never see this row.** Setup shows it only when you are installing a
**self-hosted gateway** on this machine. A normal DevThrottle install -- and every install
that uses the hosted gateway -- does not check for Tailscale and does not need it.

Tailscale is what gives a **self-hosted** gateway a web address that browsers trust as
secure, so the Cockpit on another machine (or your phone) can reach it. **It is optional
for local-only use** -- a Director without Tailscale works normally on its own machine; it
just will not appear on a remote Gateway.

- **Windows:** `winget install tailscale.Tailscale`, then log into your tailnet from the tray icon.
- The Setup app checks three things and tells you exactly which one is missing: the CLI is installed, the daemon is running and logged in, and the machine has a MagicDNS name.
- One-time per tailnet (not per machine): **MagicDNS** and **HTTPS certificates** must be enabled in the [Tailscale admin console](https://login.tailscale.com/admin/dns) under DNS.

See [Multi-Machine Setup](#multi-machine-setup-remote-access) for how this fits together.

### If a tool is not detected after installing it

Programs read your `PATH` **once at launch**. If you install a prerequisite (or fix your `PATH`) **while the Setup app is already open**:

1. Click **Re-check** on the Prerequisites screen. Recent Setup builds re-read your live `PATH` from the registry, so a just-installed tool should now show **Found**.
2. If it still shows **Not found**, close and reopen the Setup app -- it will pick up the new `PATH` on the next launch.
3. Still missing? Open a **brand-new terminal** and run the tool's confirm command above (e.g. `claude --version`). If that also fails, the tool is not actually on your `PATH` yet -- re-run its installer and make sure any "Add to PATH" option is selected.

### Optional (for specific tools)

| Requirement | Needed for |
|-------------|------------|
| FFmpeg | cc-transcribe, cc-video |
| Graphviz | cc-docgen (C4 diagrams) |
| Playwright browsers | cc-browser, cc-reddit, cc-crawl4ai |
| OpenAI API key | cc-image, cc-voice, cc-whisper, cc-computer, cc-transcribe, cc-photos |
| Google OAuth credentials | cc-gmail |
| Azure App Registration | cc-outlook |

## Install CC Tools

The fastest way to get the CLI tools is with the installer:

```bash
cc-devthrottle setup install
```

This downloads all tools from GitHub releases, places them in `%LOCALAPPDATA%\cc-director\bin\`, and adds them to your PATH. No admin privileges required.

### macOS

On macOS, use the **DevThrottle Setup** app instead. Install and open it with one command in Terminal:

```bash
curl -fsSL https://raw.githubusercontent.com/thefrederiksen/devthrottle/main/scripts/install-mac.sh | bash
```

The command downloads the wizard from the [latest release](https://github.com/thefrederiksen/devthrottle/releases/latest), verifies its SHA-256 hash against the release manifest, places **DevThrottle Setup** in `~/Applications`, and opens it.

It uses `curl` on purpose. The wizard is ad-hoc-signed, not notarized by Apple, so a **browser** download of it is blocked by Gatekeeper — "Apple could not verify 'DevThrottle Setup' is free of malware" — and on macOS 15 (Sequoia) and later that dialog offers no way to open the app (the old right-click -> Open bypass was removed). Downloads made with `curl` are never quarantined, so the wizard opens normally.

If you already downloaded the zip with a browser and hit that block, either run the command above (recommended), or open **System Settings -> Privacy & Security**, scroll down to the message about "DevThrottle Setup", and choose **Open Anyway**. Removing the quarantine flag with `xattr -dr com.apple.quarantine "DevThrottle Setup.app"` also works, but only on a freshly unzipped copy that has never been launched — macOS remembers a copy it has already blocked.

The wizard installs the Director to `~/Applications`, installs every `cc-*` tool into one shared Python environment under `~/Library/Application Support/cc-director`, and symlinks the tools into `~/.local/bin` (added to your shell `PATH`). Apple Silicon only; Workstation-only (no Gateway on macOS).

### Verify installation

After installation, open a new terminal and verify:

```bash
cc-markdown --version
cc-excel --version
cc-hardware
```

## Install the Desktop App

You do **not** build the desktop app from source to install it - the released build is what
auto-updates in place. On **Windows**, install it one of two ways:

- **Headless (recommended for an AI coding agent):** download the command-line installer
  `devthrottle-setup-cli-win-x64.exe` from the [latest release](https://github.com/thefrederiksen/devthrottle/releases/latest),
  verify its SHA-256 against `release-manifest.json`, then run it:

  ```powershell
  devthrottle-setup-cli-win-x64.exe install
  ```

  This installs the Director app, the `cc-*` tools (added to your `PATH`), and the launcher -
  per-user, no admin. The Director is a .NET 10 app, so also ensure the runtime is present:
  `winget install Microsoft.DotNet.AspNetCore.10`. Then sign in from the command line - the only
  step that needs you - with `devthrottle-setup-cli-win-x64.exe signin` (or, to join a gateway you
  already run on another machine, `devthrottle-setup-cli-win-x64.exe enroll`); it opens the browser
  to sign in or create a free account. Full agent walkthrough:
  [Installing with an AI coding agent](https://devthrottle.com/docs/install#install-agent).

- **Graphical wizard:** download **DevThrottle Setup** from your [account page](https://devthrottle.com/signup)
  and run it - it walks you through the same install.

On **macOS**, use the **DevThrottle Setup** app via the Terminal command shown above under
[macOS](#macos) (Workstation-only; no Gateway on macOS).

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

One Gateway machine runs the fleet view (the Cockpit); every other machine just runs Directors that show up there. Adding a new machine to the fleet is three steps:

1. **Install Tailscale** and log into the same tailnet (`winget install tailscale.Tailscale`, then sign in from the tray icon).
2. **Install DevThrottle** (Workstation role) with the Setup app or `cc-director-setup-cli install`.
3. **Set the Gateway URL** in the Director's Settings (or `gateway.url` in config.json), pointing at the Gateway machine, e.g. `https://your-gateway.your-tailnet.ts.net`.

That is all. The Director registers itself with the Gateway, opens its own Tailscale Serve front door for remote access, and verifies its advertised address actually answers before registering -- there are no manual `tailscale serve` commands and no firewall rules to add.

### How it works (so the troubleshooting below makes sense)

A Director listens on `localhost` only; the single remote path to it is a Tailscale Serve HTTPS mapping on its **own** machine, which each Director now provisions and self-heals for itself. The Director also refuses to register an address that does not demonstrably answer, so a misconfigured machine produces one precise error in its own log instead of a silently dead entry in the fleet.

### Troubleshooting

| Symptom | Meaning | Fix |
|---------|---------|-----|
| Cockpit: "endpoint never answered since registration -- check Tailscale Serve / the Director log on MACHINE" | The Director's machine never opened its HTTPS front door. | On that machine, check the Director log for the exact reason (see rows below); usually Tailscale is missing, logged out, or HTTPS certs are not enabled for the tailnet. |
| Director log: "tailscale CLI not found" | Tailscale is not installed on the Director's machine. | `winget install tailscale.Tailscale`, log in, restart the Director (or wait -- it retries automatically). |
| Director log: "tailscale serve --https=PORT failed: ..." | The serve command itself failed; the CLI output is included verbatim. | Most common: HTTPS certificates are not enabled for the tailnet -- enable them in the admin console under DNS -> HTTPS Certificates. |
| Director log: "NOT registering ... healthz probe timed out" | The mapping exists (or just got created) but the address does not answer yet. | First-ever serve on a machine can take seconds to get its TLS certificate; the Director retries with backoff and registers when it answers. If it never clears, check `tailscale serve status` on that machine. |
| Cockpit: "unreachable (timeout; cooling down)" | The Director WAS reachable before and went dark (machine asleep, Tailscale down, process gone). | Wake the machine / check Tailscale connectivity; the Gateway re-probes automatically. |
| Setup app: Tailscale row shows a failing check | Detection-only preflight: CLI missing, daemon stopped/logged out, or no MagicDNS name. | The row text contains the exact command to run; local-only use is unaffected. |

## Next Steps

- [Quick Start](quick-start.md) -- Walk through your first session
- [Tools Overview](../tools/overview.md) -- See all available tools
