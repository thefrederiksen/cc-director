# DevThrottle - install prompt (Windows + macOS)

The **prompt you hand to Claude Code** to install the latest DevThrottle headlessly. One prompt,
OS-aware: the agent detects the platform and uses the **command-line installer** (never the graphical
wizard) to lay everything down **per-user, with no admin/sudo**, so the built-in **auto-update can
replace it in place**. Never `Program Files` (Windows) or `/Applications` (macOS).
Philosophy: [../PHILOSOPHY.md](../PHILOSOPHY.md).

The command-line installer is the **same install engine** the graphical wizard uses, so a human and
an agent end up with an identical setup. It installs the Director app, every `cc-*` CLI tool (on your
`PATH`), and the launcher.

Copy everything in the block below into a Claude Code session on the target machine.

---

```text
Install the latest release of DevThrottle on THIS machine, headlessly. Use the command-line
installer, NOT the graphical wizard, and never require admin/sudo. Detect the OS and follow the
matching section. STOP with a clear message if any step fails; do not silently work around it or
build from source.

REPO: github.com/thefrederiksen/devthrottle  (public)
Find the latest release - prefer `gh release view --repo thefrederiksen/devthrottle --json tagName,assets`,
else the public API https://api.github.com/repos/thefrederiksen/devthrottle/releases/latest. It must
include `release-manifest.json` plus this OS's asset below. ALWAYS verify the downloaded asset's
SHA-256 against the manifest's entry for that asset before running it; mismatch = STOP. Assets are at
https://github.com/thefrederiksen/devthrottle/releases/latest/download/<asset-name>.

== WINDOWS ==
ASSETS: devthrottle-setup-cli-win-x64.exe  +  release-manifest.json  (the CLI installer is
        self-contained, so it runs before .NET is present)
1. Download both to %TEMP%\ccd-install.
2. Verify: Get-FileHash -Algorithm SHA256 of devthrottle-setup-cli-win-x64.exe == the manifest's
   sha256 for that asset, else STOP.
3. Install (per-user workstation, no admin). `prereqs` confirms a coding agent is present; `install`
   lays down the Director app, the cc-* tools (added to PATH), and the launcher under
   %LOCALAPPDATA%\cc-director. Add --json to either for machine-readable output:
      devthrottle-setup-cli-win-x64.exe prereqs
      devthrottle-setup-cli-win-x64.exe install
4. The Director is a .NET 10 app (framework-dependent). If `dotnet --list-runtimes` does not list
   Microsoft.AspNetCore.App 10, install the runtime (still no admin):
      winget install Microsoft.DotNet.AspNetCore.10

== macOS (Apple Silicon) ==
macOS has no fully-headless path today: the app is ad-hoc-signed, not Apple-notarized, so a browser
download is Gatekeeper-blocked. Run the official curl bootstrap - a curl download is never
quarantined. It downloads the setup app, verifies its SHA-256 against the manifest, installs it to
~/Applications, and opens it:
      curl -fsSL https://raw.githubusercontent.com/thefrederiksen/devthrottle/main/scripts/install-mac.sh | bash
Then complete the short setup app. macOS is workstation-only (no Gateway).

== BOTH ==
5. Launch DevThrottle once and SIGN IN. It opens devthrottle.com in the browser; the user enters a
   password there (or creates a free account) and the app captures the session automatically. That
   browser sign-in is the ONLY step a human has to do - everything else the agent runs unattended.
6. Confirm the running version matches the release tag (Windows: the newest log under
   %LOCALAPPDATA%\cc-director\logs\director\; macOS: the app's log dir). Report: the release tag
   installed, the install path, and the SHA-256 you verified.
   Note the runtime prerequisites if not set up: a Claude subscription (for Claude Code) and, for
   hosted audio/transcription/TTS, an OpenAI API key in the cc-director config dir
   (%LOCALAPPDATA%\cc-director\config\credentials.env on Windows; the equivalent config dir on macOS).

DO NOT: download or run the graphical installer on Windows (use the CLI installer), require
admin/sudo, build from source, place files in Program Files or /Applications, or skip SHA verification.
```

---

## Why this shape

- **Command-line installer, not the wizard** - the CLI front-end (`devthrottle-setup-cli`) drives the
  same install engine as the graphical wizard, so there is nothing to click through. An AI agent can
  run the whole install unattended; the one human step is the browser sign-in.
- **Per-user, user-writable target** (`%LOCALAPPDATA%\cc-director` / `~/Applications`) - the
  auto-updater overwrites the running app's own path, so a user-writable location means updates need
  **no admin/sudo**. `Program Files` / `/Applications` would force elevation on every update or fail.
- **SHA-256 against `release-manifest.json`** - the same trust check the auto-updater uses, so install
  and update share one verification.
- **The Director needs the .NET 10 runtime** (it ships framework-dependent). The CLI installer does
  not install the runtime, so the prompt ensures it on Windows. The macOS app is self-contained.
- **One OS-aware prompt** - hand the same prompt to any machine; the agent picks the right branch.

## Try it / next steps

1. Run the prompt in Claude Code on the target machine. The current latest release (with
   `devthrottle-setup-cli-win-x64.exe`, `cc-director-mac-arm64.zip` and `release-manifest.json`) is on
   the [releases page](https://github.com/thefrederiksen/devthrottle/releases/latest).
2. Cut a newer release; launch the installed build and confirm **auto-update** pulls it (the build
   must be a CI release build, i.e. `UpdaterEnabled=true`).

The public website carries the same guidance for an agent that reads it:
[devthrottle.com/docs/install#install-agent](https://devthrottle.com/docs/install#install-agent) and
`devthrottle.com/llms.txt`.
