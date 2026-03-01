# Installation

CC Director runs on Windows and requires a few prerequisites. This guide walks you through getting everything set up.

## Prerequisites

- **Windows 10/11** (64-bit)
- **Claude Code** -- the Anthropic CLI ([install guide](https://docs.anthropic.com/en/docs/claude-code/overview))
- **.NET 8 Runtime** -- for the desktop engine and some tools
- **Node.js 18+** -- for browser automation and web tools
- **Git** -- for cloning the repository

### Optional (for specific tools)

| Requirement | Needed for |
|-------------|------------|
| FFmpeg | cc-transcribe, cc-video |
| Graphviz | cc-docgen (C4 diagrams) |
| Playwright browsers | cc-browser, cc-linkedin, cc-reddit, cc-crawl4ai |
| OpenAI API key | cc-image, cc-voice, cc-whisper, cc-computer, cc-transcribe, cc-photos |
| Google OAuth credentials | cc-gmail |
| Azure App Registration | cc-outlook |

## Install CC Tools

The fastest way to get the CLI tools is with the installer:

```bash
cc-setup
```

This downloads all tools from GitHub releases, places them in `%LOCALAPPDATA%\cc-director\bin\`, and adds them to your PATH. No admin privileges required.

### Verify installation

After installation, open a new terminal and verify:

```bash
cc-markdown --version
cc-excel --version
cc-hardware
```

## Install the Desktop Engine

Clone the repository and build:

```bash
git clone https://github.com/cc-director/cc-director.git
cd cc-director
dotnet build src/CcDirector.Wpf/CcDirector.Wpf.csproj
```

Run the application:

```bash
dotnet run --project src/CcDirector.Wpf/CcDirector.Wpf.csproj
```

## Configure Claude Code Skills

CC Director includes Claude Code skills that extend what Claude can do. After cloning, the skills in `.claude/skills/` are automatically available when you run Claude Code from the repository directory.

Key skills:
- `/commit` -- create commits following project standards
- `/review-code` -- security and PII review before commits
- `/update-docs` -- keep documentation in sync with code changes

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

Install Playwright browsers (needed for cc-browser, cc-linkedin, cc-reddit):

```bash
npx playwright install chromium
```

## Environment Variables

Set the OpenAI API key for AI-powered tools:

```bash
set OPENAI_API_KEY=your-key-here
```

Or add it permanently through Windows System Properties > Environment Variables.

## Next Steps

- [Quick Start](quick-start.md) -- Walk through your first session
- [Tools Overview](../tools/overview.md) -- See all available tools
