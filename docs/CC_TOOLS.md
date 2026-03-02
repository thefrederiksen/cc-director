# CC Director Tools Reference

Command-line tools included with CC Director for document conversion, media processing, email, browser automation, desktop automation, and AI workflows.

**Install location:** `%LOCALAPPDATA%\cc-director\bin\` (all tools are on PATH)

**Shell compatibility:** Tools work in CMD, PowerShell, and Git Bash (used by Claude Code).
Node.js and .NET tools include both `.cmd` (Windows) and extensionless (Git Bash) launchers.

**Tool registry:** `tools/registry.json` is the authoritative source for the full tool inventory.

---

## Quick Reference

### Documents

| Tool | Description | Requirements |
|------|-------------|--------------|
| cc-markdown | Markdown to PDF/Word/HTML with themes | Chrome/Chromium |
| cc-excel | CSV/JSON/Markdown to formatted Excel workbooks | None (not yet built) |
| cc-powerpoint | Markdown to PowerPoint presentations | None |

### Email

| Tool | Description | Requirements |
|------|-------------|--------------|
| cc-gmail | Gmail CLI: read, send, search, labels, calendar, contacts | Google OAuth |
| cc-outlook | Outlook CLI: email, calendar, attachments, folders | Azure OAuth |

### Web and Social

| Tool | Description | Requirements |
|------|-------------|--------------|
| cc-browser | Persistent browser automation with workspaces | Node.js, Playwright |
| cc-linkedin | LinkedIn automation with human-like delays | Playwright, cc-browser |
| cc-reddit | Reddit automation with human-like delays | Playwright, cc-browser |
| cc-spotify | Spotify playback control via browser | cc-browser |
| cc-crawl4ai | AI-ready web crawler to clean markdown | Playwright browsers |
| cc-websiteaudit | Website SEO/security/AI readiness audit | Node.js, Chrome (not yet built) |
| cc-brandingrecommendations | Branding action plans from audit data | Node.js |

### Desktop Automation

| Tool | Description | Requirements |
|------|-------------|--------------|
| cc-click | Windows UI automation: click, type, screenshot | Windows, .NET |
| cc-trisight | 3-tier UI element detection (UIA + OCR + pixel) | Windows, .NET |
| cc-computer | AI desktop agent with screenshot-in-the-loop | Windows, .NET, OPENAI_API_KEY |

### Media

| Tool | Description | Requirements |
|------|-------------|--------------|
| cc-image | Image generation, analysis, OCR | OPENAI_API_KEY |
| cc-voice | Text-to-speech (OpenAI TTS) | OPENAI_API_KEY |
| cc-whisper | Audio transcription and translation | OPENAI_API_KEY |
| cc-video | Video info, audio extraction, screenshots, frames | FFmpeg |
| cc-transcribe | Video/audio transcription with screenshots | FFmpeg, OPENAI_API_KEY |
| cc-photos | Photo scanning, duplicates, AI descriptions | OPENAI_API_KEY |
| cc-youtube-info | YouTube transcript/metadata extraction | None |

### Data and Utilities

| Tool | Description | Requirements |
|------|-------------|--------------|
| cc-vault | Personal vault: contacts, tasks, goals, docs, RAG | None |
| cc-hardware | System hardware info (RAM, CPU, GPU, disk) | None |
| cc-comm-queue | Communication Manager approval queue | None |
| cc-docgen | C4 architecture diagrams from YAML | Graphviz (not yet built) |
| cc-director-setup | Windows installer for CC Director | None |
| cc-personresearch | Person research aggregation | (not yet built) |

---

## Documents

### cc-markdown

Convert Markdown to PDF, Word, or HTML with built-in themes.

```bash
cc-markdown report.md -o report.pdf
cc-markdown report.md -o report.pdf --theme boardroom
cc-markdown report.md -o report.docx
cc-markdown report.md -o report.html
cc-markdown --themes
```

**Themes:** boardroom, terminal, paper (default), spark, thesis, obsidian, blueprint

**Options:**
- `-o, --output` - Output file (format from extension: .pdf, .docx, .html) *required*
- `--theme, -t` - Theme name (default: paper)
- `--css` - Custom CSS file
- `--page-size` - a4 or letter (default: a4)
- `--margin` - Page margin (default: 1in)

---

### cc-excel

Convert CSV, JSON, and Markdown tables to formatted Excel workbooks with themes, charts, and formulas.

**Status:** Source exists but not yet built.

```bash
# CSV to Excel
cc-excel from-csv sales.csv -o sales.xlsx
cc-excel from-csv sales.csv -o sales.xlsx --theme boardroom
cc-excel from-csv data.csv -o report.xlsx --delimiter ";" --encoding utf-8
cc-excel from-csv data.csv -o report.xlsx --no-header
cc-excel from-csv data.csv -o report.xlsx --sheet-name "Q4 Sales"

# JSON to Excel
cc-excel from-json api-response.json -o report.xlsx
cc-excel from-json data.json -o report.xlsx --theme terminal
cc-excel from-json nested.json -o report.xlsx --json-path "$.results"

# Markdown tables to Excel
cc-excel from-markdown report.md -o report.xlsx
cc-excel from-markdown report.md -o report.xlsx --all-tables
cc-excel from-markdown report.md -o report.xlsx --table-index 2

# Charts
cc-excel from-csv sales.csv -o chart.xlsx --chart bar --chart-x 0 --chart-y 1

# Summary rows
cc-excel from-csv sales.csv -o report.xlsx --summary all

# Conditional highlighting
cc-excel from-csv sales.csv -o report.xlsx --highlight best-worst
cc-excel from-csv sales.csv -o report.xlsx --highlight scale

# Multi-sheet workbook from JSON spec
cc-excel from-spec workbook.json -o output.xlsx

# List themes
cc-excel --themes
```

**Subcommands:** `from-csv`, `from-json`, `from-markdown`, `from-spec`

**Themes:** boardroom, paper (default), terminal, spark, thesis, obsidian, blueprint

---

### cc-powerpoint

Convert Markdown to PowerPoint presentations with built-in themes.

```bash
cc-powerpoint slides.md -o presentation.pptx
cc-powerpoint slides.md -o deck.pptx --theme boardroom
cc-powerpoint slides.md
cc-powerpoint --themes
```

**Slide syntax:** Use `---` to separate slides. First slide with `# Title` and optional `## Subtitle` becomes the title slide. Speaker notes use blockquotes (`> note text`).

**Themes:** boardroom, paper (default), terminal, spark, thesis, obsidian, blueprint

**Options:**
- `-o, --output` - Output .pptx file path
- `--theme, -t` - Theme name (default: paper)

---

## Email

### cc-gmail

Gmail CLI with multi-account support.

```bash
# Setup
cc-gmail accounts add personal --default
cc-gmail auth

# List emails
cc-gmail list
cc-gmail list --unread
cc-gmail list -l SENT

# Read email
cc-gmail read <message_id>

# Send email
cc-gmail send -t "to@example.com" -s "Subject" -b "Body text"
cc-gmail send -t "to@example.com" -s "Subject" -f body.txt
cc-gmail send -t "to@example.com" -s "Subject" -b "See attached" -a file.pdf

# Search
cc-gmail search "from:someone@example.com"
cc-gmail search "subject:important is:unread"

# Draft
cc-gmail draft -t "to@example.com" -s "Subject" -b "Draft body"

# Reply (creates draft reply)
cc-gmail reply <message_id>

# List drafts
cc-gmail drafts

# Email management
cc-gmail delete <message_id>
cc-gmail untrash <message_id>
cc-gmail archive <message_id>
cc-gmail archive-before 2024-01-01
cc-gmail move <message_id> --label "Projects"

# Labels
cc-gmail labels
cc-gmail label-create "New Label"

# Stats
cc-gmail count "is:unread"
cc-gmail stats
cc-gmail label-stats "INBOX"

# Calendar (OAuth only)
cc-gmail calendar

# Contacts (OAuth only)
cc-gmail contacts

# Profile
cc-gmail profile

# Use specific account
cc-gmail -a work list
```

**Search syntax:** `from:`, `to:`, `subject:`, `is:unread`, `has:attachment`, `after:YYYY/MM/DD`, `before:YYYY/MM/DD`

---

### cc-outlook

Outlook CLI with email and calendar support via Microsoft Graph API.

```bash
# Setup
cc-outlook accounts add your@email.com --client-id YOUR_CLIENT_ID
cc-outlook auth

# List inbox
cc-outlook list
cc-outlook list --unread
cc-outlook list -f sent

# Read email
cc-outlook read <message_id>

# Send email
cc-outlook send -t "to@example.com" -s "Subject" -b "Body text"
cc-outlook send -t "to@example.com" -s "Report" -b "See attached" --attach report.pdf

# Reply and forward
cc-outlook reply <message_id>
cc-outlook forward <message_id>

# Search
cc-outlook search "project update"

# Email management
cc-outlook delete <message_id>
cc-outlook archive <message_id>
cc-outlook unarchive <message_id>
cc-outlook flag <message_id>
cc-outlook categorize <message_id>

# Attachments
cc-outlook attachments <message_id>
cc-outlook download-attachment <message_id>

# Folders
cc-outlook folders

# Calendar
cc-outlook calendar events                           # Next 7 days
cc-outlook calendar events -d 14                     # Next 14 days
cc-outlook calendar events --start 2025-06-01 --end 2025-12-31
cc-outlook calendar today                            # Today's agenda
cc-outlook calendar week                             # This week
cc-outlook calendar search "standup"
cc-outlook calendar freebusy "john@company.com"
cc-outlook calendar forward "EVENT_ID" -t "colleague@company.com"
cc-outlook calendar create -s "Meeting" -d 2024-12-25 -t 14:00

# Profile
cc-outlook profile
```

---

## Web and Social

### cc-browser

Persistent browser automation with workspace management. Runs as a daemon for persistent sessions.

```bash
# Daemon
cc-browser daemon
cc-browser status

# Browser management
cc-browser browsers                                  # List available browsers
cc-browser profiles [--browser chrome]               # List system profiles
cc-browser workspaces                                # List workspaces
cc-browser favorites --workspace work                # Get favorites

# Launch
cc-browser start --workspace myworkspace
cc-browser start --browser edge --workspace work
cc-browser start --incognito
cc-browser stop

# Navigation
cc-browser navigate --url "https://example.com"
cc-browser reload
cc-browser back / forward

# Page inspection
cc-browser snapshot [--interactive]
cc-browser info
cc-browser text [--selector ".content"]
cc-browser html [--selector ".content"]

# Interactions
cc-browser click --ref <e1>
cc-browser click --text "Submit"
cc-browser click --selector ".btn"
cc-browser type --ref <e1> --text "hello"
cc-browser press --key Enter
cc-browser hover --ref <e1>
cc-browser select --ref <e1> --value "option"
cc-browser scroll [--direction down]

# Screenshots
cc-browser screenshot [--fullPage]
cc-browser screenshot --save ./page.png
cc-browser screenshot-labels

# Tabs
cc-browser tabs
cc-browser tabs-open [--url <url>]
cc-browser tabs-close --tab <targetId>
cc-browser tabs-focus --tab <targetId>

# Mode (speed/detection)
cc-browser mode human
cc-browser mode fast
cc-browser mode stealth

# Record and replay
cc-browser record start
cc-browser record stop --output flow.json
cc-browser replay --file flow.json

# CAPTCHA
cc-browser captcha detect
cc-browser captcha solve

# Sessions
cc-browser session create --name <name>
cc-browser session list
cc-browser session close --session <id>

# JavaScript
cc-browser evaluate --js "document.title"

# Advanced
cc-browser wait --text "loaded"
cc-browser fill --fields '[...]'
cc-browser upload --ref <e1> --path <file>
```

**Note:** NEVER use cc-browser directly with LinkedIn or Reddit. Use cc-linkedin and cc-reddit instead.

---

### cc-linkedin

LinkedIn automation CLI with human-like delays. Communicates through cc-browser daemon.

```bash
# Status
cc-linkedin status
cc-linkedin whoami
cc-linkedin me

# Feed and posts
cc-linkedin feed --limit 10
cc-linkedin post <url>
cc-linkedin create "Post content here"
cc-linkedin create "Post with image" --image photo.png
cc-linkedin like <url>
cc-linkedin comment <url>
cc-linkedin repost <url>
cc-linkedin save <url>

# Profiles and connections
cc-linkedin profile <username>
cc-linkedin connections --limit 20
cc-linkedin connect <username>
cc-linkedin company <name>

# Messages
cc-linkedin messages --unread
cc-linkedin message <username> --text "Hello"

# Search
cc-linkedin search "query" --type people
cc-linkedin search "query" --type companies

# Invitations
cc-linkedin invitations
cc-linkedin accept <invitation>
cc-linkedin ignore <invitation>

# Notifications
cc-linkedin notifications

# Jobs
cc-linkedin jobs "search query"

# Navigation and debugging
cc-linkedin goto <url>
cc-linkedin snapshot
cc-linkedin screenshot
```

**Note:** NEVER use cc-browser directly with LinkedIn. Always use cc-linkedin.

---

### cc-reddit

Reddit automation CLI with human-like delays and random jitter.

```bash
# Status
cc-reddit status
cc-reddit whoami

# Your content
cc-reddit me --posts
cc-reddit me --comments
cc-reddit saved
cc-reddit karma

# Browse
cc-reddit feed
cc-reddit post <url>

# Interact
cc-reddit comment <url> "Comment text"
cc-reddit reply <url> "Reply text"
cc-reddit upvote <url>
cc-reddit downvote <url>

# Subreddits
cc-reddit join <subreddit>
cc-reddit leave <subreddit>

# Navigation and debugging
cc-reddit goto <url>
cc-reddit snapshot
cc-reddit screenshot
```

**Note:** NEVER use cc-browser directly with Reddit. Always use cc-reddit.

---

### cc-spotify

Spotify CLI via browser automation. Controls Spotify Web Player through a cc-browser workspace.

```bash
# Setup
cc-spotify config --workspace edge-personal

# Status
cc-spotify status
cc-spotify now

# Playback
cc-spotify play
cc-spotify pause
cc-spotify next
cc-spotify prev

# Controls
cc-spotify shuffle --on / --off
cc-spotify repeat off                    # off, context, track
cc-spotify volume 75                     # 0-100
cc-spotify like                          # Heart current track

# Browse
cc-spotify search "Miles Davis"
cc-spotify playlists                     # List sidebar playlists
cc-spotify playlist "Chill Vibes"        # Play by name
cc-spotify queue                         # Show queue
cc-spotify liked                         # List liked songs
cc-spotify goto <url>

# Recommendations
cc-spotify recommend
cc-spotify recommend --mood "chill jazz"

# Config
cc-spotify config --workspace NAME
cc-spotify config --show
```

**Setup:** Requires cc-browser daemon running with Spotify Web Player open and logged in.

---

### cc-crawl4ai

AI-ready web crawler: crawl pages to clean markdown for LLM/RAG workflows.

```bash
# Crawl a URL
cc-crawl4ai crawl "https://example.com"
cc-crawl4ai crawl <url> -o page.md

# Fit markdown (noise filtered)
cc-crawl4ai crawl <url> --fit

# Batch crawl from file
cc-crawl4ai batch urls.txt -o ./output/

# Stealth mode
cc-crawl4ai crawl <url> --stealth

# Wait for dynamic content
cc-crawl4ai crawl <url> --wait-for ".content-loaded"

# Scroll full page
cc-crawl4ai crawl <url> --scroll

# Extract specific CSS selector
cc-crawl4ai crawl <url> --css "article.main"

# Take screenshot
cc-crawl4ai crawl <url> --screenshot

# Authenticated sessions
cc-crawl4ai session create mysite -u "https://example.com/login" --interactive
cc-crawl4ai crawl <url> --session mysite
```

---

### cc-websiteaudit

Comprehensive website auditing: SEO, security, structured data, and AI readiness.

**Status:** Source exists but not yet built.

```bash
cc-websiteaudit https://example.com
cc-websiteaudit example.com -o report.pdf
cc-websiteaudit example.com --format json -o audit.json
cc-websiteaudit example.com --format markdown -o audit.md
cc-websiteaudit example.com --pages 50 --depth 4 --verbose
cc-websiteaudit example.com --modules technical-seo,security
cc-websiteaudit example.com --quiet
```

**Modules:** technical-seo (20%), on-page-seo (20%), security (10%), structured-data (10%), ai-readiness (20%)

**Grades:** A+ (97+) through F (<60)

---

### cc-brandingrecommendations

Reads cc-websiteaudit JSON output and produces prioritized, week-by-week branding action plans.

```bash
cc-brandingrecommendations --audit report.json
cc-brandingrecommendations --audit report.json --format json
cc-brandingrecommendations --audit report.json -o plan.md
cc-brandingrecommendations --audit report.json --budget high --industry saas --keywords "project management" --competitors "asana.com,monday.com"
```

**Options:**
- `--audit <path>` - Path to cc-websiteaudit JSON report *required*
- `-o, --output <path>` - Output file (auto-detects format from extension)
- `--format` - console, json, markdown (default: console)
- `--budget` - low (5h), medium (10h), high (20h) (default: medium)
- `--industry` - Industry vertical
- `--keywords` - Comma-separated target keywords
- `--competitors` - Comma-separated competitor domains

**Workflow:**
```bash
cc-websiteaudit example.com --format json -o audit.json
cc-brandingrecommendations --audit audit.json -o plan.md
cc-markdown plan.md -o plan.pdf --theme boardroom
```

---

## Desktop Automation

Three tools work together for AI-powered desktop automation:

```
cc-computer (AI Agent - the "brain")
    |-- LLM-powered (GPT via Semantic Kernel)
    |-- Screenshot-in-the-loop verification
    |-- Evidence chain logging
    |
    +-- uses TrisightCore (3-tier detection library)
    |       |-- UI Automation + OCR + Pixel Analysis
    |       +-- 98.9% element clickability accuracy
    |
    +-- calls cc-click for actions

cc-trisight (Detection CLI - the "eyes")
    |-- UI element detection with numbered bounding boxes
    +-- uses TrisightCore (same shared library)

cc-click (Automation CLI - the "hands")
    |-- Click, type, screenshot, read text
    +-- Low-level Windows UI automation
```

**When to use which:**
- `cc-click` - Direct automation when you know exactly what to click/type
- `cc-trisight` - Inspect UI elements, get coordinates for scripting
- `cc-computer` - Natural language tasks: "Open Notepad and save a file called test.txt"

### cc-click

Windows UI automation for clicking, typing, screenshots, and reading text.

```bash
cc-click click <element>
cc-click type <text>
cc-click screenshot
cc-click read-text <element>
cc-click list-windows
cc-click list-elements <window>
```

**Commands:**
- `click` - Click a UI element
- `type` - Type text into a UI element
- `screenshot` - Capture a screenshot
- `read-text` - Read text content of a UI element
- `list-windows` - List visible top-level windows
- `list-elements` - List UI elements in a window

---

### cc-trisight

Three-tier UI element detection for Windows.

```bash
# Full detection pipeline
trisight detect --window "Notepad" --annotate --output annotated.png

# UIA only
trisight uia --window "Notepad"

# OCR only
trisight ocr --screenshot page.png

# Annotate existing screenshot
trisight annotate --screenshot page.png --window "Notepad" --output annotated.png
```

**Commands:** `detect`, `uia`, `ocr`, `annotate`

**Options:**
- `--window, -w` - Target window title (substring match)
- `--tiers` - Detection tiers: uia,ocr,pixel (default: all)
- `--depth, -d` - Max UIA tree depth (default: 15)
- `--annotate` - Generate annotated screenshot with numbered boxes
- `--output, -o` - Output path for annotated image
- `--screenshot` - Path to existing screenshot

---

### cc-computer

AI desktop automation agent using TriSight 3-tier visual detection.

```bash
# Run a task in CLI mode
cc-computer "Open Notepad and type Hello World"

# Interactive REPL mode
cc-computer

# Launch GUI mode
cc-computer-gui
```

**How it works:**
1. Takes a screenshot
2. Detects UI elements (buttons, text fields, etc.)
3. Overlays numbered bounding boxes on screenshot
4. LLM sees annotated screenshot + element list
5. LLM issues action (click, type, shortcut)
6. Agent executes action, captures new screenshot
7. Loop until task complete

**Dependencies:** cc-click, cc-trisight (shared TrisightCore library)

---

## Media

### cc-image

Image generation, analysis, and OCR using OpenAI.

**Status:** BROKEN - needs rebuild (missing cc_storage module).

```bash
cc-image generate "A sunset over mountains" -o sunset.png
cc-image describe image.png
cc-image ocr screenshot.png
```

---

### cc-voice

Text-to-speech using OpenAI TTS.

```bash
cc-voice "Hello, world!" -o hello.mp3
cc-voice "Hello" -o hello.mp3 --voice nova
cc-voice "Long text" -o speech.mp3 --speed 1.5
cc-voice message.txt -o speech.mp3
cc-voice "Raw markdown text" -o speech.mp3 --raw
```

**Voices:** alloy, echo, fable, nova, onyx (default), shimmer

**Options:**
- `-o, --output` - Output audio file (.mp3) *required*
- `--voice, -v` - Voice name (default: onyx)
- `--model, -m` - Model: tts-1, tts-1-hd (default: tts-1)
- `--speed, -s` - Speed: 0.25 to 4.0 (default: 1.0)
- `--raw` - Don't clean markdown formatting

---

### cc-whisper

Audio transcription and translation using OpenAI Whisper.

```bash
cc-whisper transcribe audio.mp3
cc-whisper transcribe audio.mp3 -o transcript.txt
cc-whisper translate foreign-audio.mp3
```

**Commands:**
- `transcribe` - Transcribe audio using OpenAI Whisper
- `translate` - Translate audio from any language to English

---

### cc-video

Video utilities: info, audio extraction, screenshots, and frame extraction.

```bash
cc-video info video.mp4
cc-video audio video.mp4 -o audio.mp3
cc-video screenshots video.mp4
cc-video frame video.mp4 --timestamp 01:30
```

**Commands:**
- `info` - Show video information
- `audio` - Extract audio from video
- `screenshots` - Extract screenshots at content changes
- `frame` - Extract single frame at timestamp

---

### cc-transcribe

Transcribe video/audio with timestamps and extract screenshots at content changes.

```bash
cc-transcribe video.mp4
cc-transcribe video.mp4 -o ./output/
cc-transcribe video.mp4 --no-screenshots
cc-transcribe video.mp4 --threshold 0.85
cc-transcribe video.mp4 --info
```

**Output:** transcript.txt, transcript.json, screenshots/

**Options:**
- `-o, --output` - Output directory
- `--no-screenshots` - Skip screenshot extraction
- `--threshold, -t` - SSIM 0.0-1.0 (default: 0.92, lower = more screenshots)
- `--interval, -i` - Min seconds between screenshots (default: 1.0)
- `--language, -l` - Force language code (e.g., en, es, fr)
- `--info` - Show video info and exit

---

### cc-photos

Photo organization: scan directories, detect duplicates and screenshots, AI descriptions.

```bash
# Sources
cc-photos source add "D:\Photos" --category private --label "Family" --priority 1
cc-photos source list
cc-photos source remove "Family"

# Scanning
cc-photos scan
cc-photos scan --source "Family"
cc-photos discover

# Exclusions
cc-photos exclude

# Duplicates
cc-photos dupes
cc-photos dupes --cleanup
cc-photos dupes --review

# Listing
cc-photos list --category private
cc-photos list --screenshots

# AI analysis
cc-photos analyze
cc-photos analyze --limit 50
cc-photos analyze --provider openai

# Search
cc-photos search "beach vacation"

# Stats
cc-photos stats
```

**Commands:**
- `source` - Manage photo sources
- `scan` - Scan drives for photos
- `discover` - Find where photos are without adding to database
- `exclude` - Manage excluded paths
- `dupes` - Find and manage duplicates
- `list` - List images in database
- `search` - Search image descriptions
- `analyze` - Generate AI descriptions
- `stats` - Show database statistics

---

### cc-youtube-info

Extract transcripts, metadata, and chapters from YouTube videos.

```bash
cc-youtube-info info <url>
cc-youtube-info info <url> --json
cc-youtube-info transcript <url>
cc-youtube-info transcript <url> -o transcript.txt
cc-youtube-info transcript <url> --format srt -o captions.srt
cc-youtube-info languages <url>
cc-youtube-info chapters <url>
```

**Commands:** `info`, `transcript`, `languages`, `chapters`

**Options:**
- `-o, --output` - Output file
- `-l, --lang` - Language code (default: en)
- `-f, --format` - txt, srt, or vtt
- `--json` - Output as JSON
- `--no-timestamps` - Remove timestamps

---

## Data and Utilities

### cc-vault

Personal data vault with contacts, documents, tasks, goals, ideas, and RAG search.

```bash
# Search and ask
cc-vault search "query"
cc-vault ask "question"

# Contacts
cc-vault contacts list
cc-vault contacts list --account personal
cc-vault contacts list --account consulting
cc-vault contacts show <id>
cc-vault contacts search "name"
cc-vault contacts add "Name" -e "email@example.com"
cc-vault contacts update <id>
cc-vault contacts memory <id>

# Documents
cc-vault docs list
cc-vault docs import file.pdf

# Tasks, goals, ideas
cc-vault tasks list
cc-vault goals list
cc-vault ideas list

# Health data
cc-vault health

# Social media posts
cc-vault posts

# Contact lists
cc-vault lists list
cc-vault lists create "Name" -d "Description" -t type
cc-vault lists show "Name"
cc-vault lists rename "Old Name" "New Name"
cc-vault lists update "Name" -d "New desc" -t type
cc-vault lists copy "Source" "Dest"
cc-vault lists add "Name" -c 42
cc-vault lists add "Name" -q "company = 'Acme'"
cc-vault lists remove "Name" -c 42
cc-vault lists export "Name" -f json
cc-vault lists delete "Name" -y

# Entity linking
cc-vault link
cc-vault unlink
cc-vault links
cc-vault context

# Graph
cc-vault graph

# Maintenance
cc-vault stats
cc-vault backup
cc-vault restore
cc-vault repair-vectors
cc-vault config
cc-vault init
```

---

### cc-hardware

Query system hardware information: RAM, CPU, GPU, disk, OS, network, battery.

```bash
cc-hardware
cc-hardware ram
cc-hardware cpu
cc-hardware gpu
cc-hardware disk
cc-hardware os
cc-hardware network
cc-hardware battery
cc-hardware --json
cc-hardware cpu --json
```

**Commands:** (none) = all, `ram`, `cpu`, `gpu`, `disk`, `os`, `network`, `battery`

**Options:** `--json, -j` for JSON output

**Notes:** GPU info requires NVIDIA GPU with drivers. Battery info only on laptops.

---

### cc-comm-queue

CLI tool for adding content to the Communication Manager approval queue.

```bash
# Add content
cc-comm-queue add linkedin post "Content..." --persona mindzie --tags "tag1,tag2"
cc-comm-queue add linkedin comment "Great insights!" --context-url "https://..."
cc-comm-queue add email email "Hi Sarah..." --email-to "sarah@co.com" --email-subject "Subject"
cc-comm-queue add reddit post "Content..." --reddit-subreddit "r/sub" --reddit-title "Title"

# Add from JSON
cc-comm-queue add-json content.json
cat content.json | cc-comm-queue add-json -

# View queue
cc-comm-queue list --status pending
cc-comm-queue status
cc-comm-queue show <id>

# Migrate
cc-comm-queue migrate

# Configuration
cc-comm-queue config show
cc-comm-queue config set queue_path "D:/path/to/content"
cc-comm-queue config set default_persona mindzie
```

**Platforms:** linkedin, twitter, reddit, youtube, email, blog

**Personas:** mindzie, consulting, personal

---

### cc-docgen

Generate C4 architecture diagrams from YAML manifest files.

**Status:** Source exists but not yet built.

```bash
cc-docgen generate
cc-docgen generate --manifest ./docs/architecture.yaml
cc-docgen generate --output ./docs/ --format svg
cc-docgen validate
```

**Output:** context.png and container.png (C4 Level 1 and Level 2 diagrams)

**Requires:** Graphviz installed and on PATH

---

### cc-director-setup

Windows installer for the CC Director tools suite.

```bash
cc-director-setup
```

Downloads tools from GitHub releases, configures PATH, installs Claude Code skill. No admin privileges required.

---

## Environment Variables

```bash
# Required for AI-powered tools (cc-image, cc-voice, cc-whisper, cc-transcribe, cc-photos, cc-computer)
set OPENAI_API_KEY=your-key-here
```

---

## Requirements Summary

| Requirement | Tools that need it |
|-------------|-------------------|
| None | cc-powerpoint, cc-vault, cc-hardware, cc-comm-queue, cc-youtube-info, cc-director-setup |
| OPENAI_API_KEY | cc-image, cc-voice, cc-whisper, cc-transcribe, cc-photos, cc-computer |
| FFmpeg | cc-video, cc-transcribe |
| Chrome/Chromium | cc-markdown, cc-websiteaudit |
| Node.js + Playwright | cc-browser, cc-websiteaudit, cc-brandingrecommendations |
| cc-browser | cc-linkedin, cc-reddit, cc-spotify |
| Google OAuth | cc-gmail |
| Azure OAuth | cc-outlook |
| Windows + .NET | cc-click, cc-trisight, cc-computer |
| Graphviz | cc-docgen |

---

## Source Repository

GitHub: https://github.com/cc-director/cc-director
