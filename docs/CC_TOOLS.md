# CC Tools Reference

Command-line tools for document conversion, media processing, email, and AI workflows.

**Install location:** `%LOCALAPPDATA%\cc-director\bin\` (tools are on PATH)

**Shell compatibility:** Tools work in CMD, PowerShell, and Git Bash (used by Claude Code).
Node.js and .NET tools include both `.cmd` (Windows) and extensionless (Git Bash) launchers.

---

## Quick Reference

| Tool | Description | Requirements |
|------|-------------|--------------|
| cc-brandingrecommendations | Branding recommendations from website audit | Node.js |
| cc-browser | Persistent browser automation with profiles | Node.js, Playwright |
| cc-click | Windows UI automation (click, type, inspect) | Windows, .NET |
| cc-comm-queue | Communication Manager queue CLI | None |
| cc-computer | AI desktop automation agent with TriSight detection | Windows, .NET, OPENAI_API_KEY |
| cc-crawl4ai | AI-ready web crawler to clean markdown | Playwright browsers |
| cc-docgen | C4 architecture diagram generator from YAML | Graphviz |
| cc-excel | CSV/JSON/Markdown to formatted Excel workbooks | None |
| cc-gmail | Gmail CLI: read, send, search emails | Google OAuth |
| cc-hardware | System hardware info (RAM, CPU, GPU, disk) | None (NVIDIA for GPU) |
| cc-image | Image generation/analysis/OCR | OpenAI API key |
| cc-linkedin | LinkedIn automation | Playwright browsers |
| cc-markdown | Markdown to PDF/Word/HTML | Chrome/Chromium |
| cc-outlook | Outlook CLI: email + calendar | Azure OAuth |
| cc-powerpoint | Markdown to PowerPoint presentations | None |
| cc-photos | Photo organization: duplicates, screenshots, AI | OpenAI API key |
| cc-reddit | Reddit automation | Playwright browsers |
| cc-spotify | Spotify web player control via browser | cc-browser |
| cc-setup | Windows installer for cc-tools suite | None |
| cc-transcribe | Video/audio transcription with screenshots | FFmpeg, OpenAI API key |
| cc-trisight | Windows screen detection and automation | Windows, .NET |
| cc-vault | Secure credential and data storage | None |
| cc-video | Video utilities | FFmpeg |
| cc-voice | Text-to-speech | OpenAI API key |
| cc-websiteaudit | Comprehensive website SEO/security/AI audit | Node.js, Chrome |
| cc-whisper | Audio transcription | OpenAI API key |
| cc-youtube-info | YouTube transcript/metadata extraction | None |

---

## Desktop Automation Stack

Three tools work together to provide AI-powered desktop automation:

```
cc-computer (AI Agent - the "brain")
    |-- Decides what to do next based on screen state
    |-- LLM-powered (GPT via Semantic Kernel)
    |-- Screenshot-in-the-loop verification
    |-- Evidence chain logging for audit
    |
    +-- uses TrisightCore (shared detection library)
    |       |-- 3-tier detection: UI Automation + OCR + Pixel Analysis
    |       +-- 98.9% element clickability accuracy
    |
    +-- calls cc-click for actions

cc-trisight (Detection CLI - the "eyes")
    |-- Standalone tool for UI element detection
    |-- Takes screenshot, returns element list with coordinates
    |-- Outputs annotated screenshots with bounding boxes
    +-- uses TrisightCore (same shared library)

cc-click (Automation CLI - the "hands")
    |-- Click at coordinates
    |-- Type text, send keystrokes
    |-- List windows, focus windows
    +-- Low-level Windows UI automation
```

**When to use which:**
- `cc-click` - Direct automation when you know exactly what to click/type
- `cc-trisight` - Inspect UI elements, get coordinates for scripting
- `cc-computer` - Natural language tasks: "Open Notepad and save a file called test.txt"

---

## cc-markdown

Convert Markdown to PDF, Word, or HTML with built-in themes.

```bash
# Convert to PDF
cc-markdown report.md -o report.pdf

# Use a theme
cc-markdown report.md -o report.pdf --theme boardroom

# Convert to Word
cc-markdown report.md -o report.docx

# Convert to HTML
cc-markdown report.md -o report.html

# List themes
cc-markdown --themes
```

**Themes:** boardroom, terminal, paper, spark, thesis, obsidian, blueprint

**Options:**
- `-o, --output` - Output file (format from extension)
- `--theme` - Theme name
- `--css` - Custom CSS file
- `--page-size` - a4 or letter (default: a4)
- `--margin` - Page margin (default: 1in)

---

## cc-excel

Convert CSV, JSON, and Markdown tables to formatted Excel workbooks with themes. Generate complex multi-sheet workbooks with formulas from JSON spec files.

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
cc-excel from-markdown report.md -o report.xlsx --theme boardroom
cc-excel from-markdown report.md -o report.xlsx --all-tables
cc-excel from-markdown report.md -o report.xlsx --table-index 2

# Charts
cc-excel from-csv sales.csv -o chart.xlsx --chart bar --chart-x 0 --chart-y 1
cc-excel from-csv sales.csv -o chart.xlsx --chart line --chart-x Quarter --chart-y Revenue --chart-y Profit

# Summary rows (formula rows at bottom)
cc-excel from-csv sales.csv -o report.xlsx --summary sum
cc-excel from-csv sales.csv -o report.xlsx --summary avg
cc-excel from-csv sales.csv -o report.xlsx --summary all    # SUM + AVG + MIN + MAX

# Conditional highlighting
cc-excel from-csv sales.csv -o report.xlsx --highlight best-worst  # green MIN, red MAX
cc-excel from-csv sales.csv -o report.xlsx --highlight scale       # 2-color gradient

# Combined
cc-excel from-csv sales.csv -o report.xlsx --summary all --highlight scale --theme boardroom

# Multi-sheet workbook from JSON spec (formulas, merges, named ranges)
cc-excel from-spec workbook.json -o output.xlsx
cc-excel from-spec workbook.json -o output.xlsx --theme boardroom

# Global options
cc-excel --version
cc-excel --themes
```

**Subcommands:**
- `from-csv` - Convert CSV to Excel
- `from-json` - Convert JSON to Excel
- `from-markdown` - Convert Markdown pipe tables to Excel
- `from-spec` - Generate multi-sheet workbook from JSON specification

**Features:**
- Auto-detects column types (integer, float, percentage, currency, date, boolean)
- Autofilter and freeze panes enabled by default
- Auto-sized columns based on content
- Alternating row shading from theme
- Optional charts (bar, line, pie, column)
- Summary formula rows (SUM, AVG, MIN, MAX) for numeric columns
- Conditional formatting (best-worst highlighting, color scales)
- Multi-sheet workbooks with formulas, merged cells, named ranges (via from-spec)

**Themes:** boardroom, paper (default), terminal, spark, thesis, obsidian, blueprint

**Shared options (from-csv, from-json, from-markdown):**
- `-o, --output` - Output .xlsx file path (required)
- `--theme, -t` - Theme name (default: paper)
- `--sheet-name` - Worksheet tab name
- `--no-autofilter` - Disable autofilter
- `--no-freeze` - Disable freeze panes
- `--summary` - Add summary rows: sum, avg, or all (sum+avg+min+max)
- `--highlight` - Conditional formatting: best-worst or scale

**from-csv options:**
- `--delimiter` - CSV delimiter (default: ,)
- `--encoding` - File encoding (default: utf-8)
- `--no-header` - First row is data, not headers

**from-json options:**
- `--json-path` - Dot-path to data array (e.g. "data" or "$.results")

**from-markdown options:**
- `--table-index` - Which table to extract (0-based, default: 0)
- `--all-tables` - Extract all tables as separate sheets

**Chart options (from-csv, from-json):**
- `--chart` - Chart type: bar, line, pie, column
- `--chart-x` - Column name or index for categories
- `--chart-y` - Column name(s) or index(es) for values (repeatable)

**from-spec options:**
- `-o, --output` - Output .xlsx file path (required)
- `--theme, -t` - Theme (overrides spec theme; default: paper)

**JSON Spec Format (from-spec):**

The spec is a JSON file describing a complete multi-sheet workbook declaratively. Designed to be LLM-friendly.

```json
{
  "theme": "boardroom",
  "named_ranges": {"annual_km": "INPUT!$B$5"},
  "sheets": [
    {
      "name": "Sheet1",
      "columns": [30, 15, 20],
      "freeze": [1, 0],
      "rows": [
        {"merge": 3, "value": "Title", "style": "title"},
        {"style": "header", "cells": ["Name", "Value", "Unit"]},
        {"cells": ["Param", {"v": 100, "fmt": "#,##0", "style": "input"}, "units"]},
        {"style": "total", "cells": ["Total", {"f": "=SUM(B3:B3)"}, null]}
      ],
      "conditional_formats": [
        {"range": "B4:C4", "type": "color_scale", "min_color": "#63BE7B", "max_color": "#F8696B"}
      ]
    }
  ]
}
```

Cell values: string, number, boolean, null (empty), or object with keys:
- `v` - static value, `f` - Excel formula, `fmt` - number format
- `style` - input/total/best/worst/accent, `merge` - span N columns
- `comment` - cell note

Row styles: header, subheader, title, subtitle, total

See `samples/workbook-spec.json` for a complete vehicle comparison example.

---

## cc-powerpoint

Convert Markdown to PowerPoint presentations with built-in themes.

```bash
# Convert to PPTX
cc-powerpoint slides.md -o presentation.pptx

# Use a theme
cc-powerpoint slides.md -o deck.pptx --theme boardroom

# Default output (same name as input, .pptx extension)
cc-powerpoint slides.md

# List themes
cc-powerpoint --themes
```

**Slide syntax:** Use `---` to separate slides. First slide with `# Title` and optional `## Subtitle` becomes the title slide. Subsequent slides auto-detect layout from content (bullets, tables, code fences, images). Speaker notes use blockquotes (`> note text`) at the end of a slide.

**Themes:** boardroom, paper (default), terminal, spark, thesis, obsidian, blueprint

**Options:**
- `-o, --output` - Output .pptx file path
- `--theme` - Theme name (default: paper)

---

## cc-transcribe

Transcribe video/audio with timestamps and extract screenshots at content changes.

```bash
# Basic transcription
cc-transcribe video.mp4

# Specify output directory
cc-transcribe video.mp4 -o ./output/

# Without screenshots
cc-transcribe video.mp4 --no-screenshots

# More frequent screenshots (lower threshold)
cc-transcribe video.mp4 --threshold 0.85

# Get video info only
cc-transcribe video.mp4 --info
```

**Output:** transcript.txt, transcript.json, screenshots/

**Options:**
- `-o, --output` - Output directory
- `--no-screenshots` - Skip screenshot extraction
- `--threshold` - SSIM 0.0-1.0 (default: 0.92, lower = more screenshots)
- `--interval` - Min seconds between screenshots (default: 1.0)
- `--language` - Force language code (e.g., en, es, fr)

---

## cc-gmail

Gmail CLI with multi-account support.

```bash
# Setup
cc-gmail accounts add personal --default
cc-gmail auth

# List inbox
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

# Profile
cc-gmail profile

# Use specific account
cc-gmail -a work list
```

**Search syntax:** `from:`, `to:`, `subject:`, `is:unread`, `has:attachment`, `after:YYYY/MM/DD`, `before:YYYY/MM/DD`

---

## cc-hardware

Query system hardware information: RAM, CPU, GPU, disk, OS, network, battery.

```bash
# Show all hardware info
cc-hardware

# Individual components
cc-hardware ram
cc-hardware cpu
cc-hardware gpu
cc-hardware disk
cc-hardware os
cc-hardware network
cc-hardware battery

# JSON output (for scripting)
cc-hardware --json
cc-hardware cpu --json

# Version
cc-hardware --version
```

**Commands:**
- `cc-hardware` - Show all hardware summary
- `cc-hardware ram` - RAM total/used/available
- `cc-hardware cpu` - CPU model, cores, usage
- `cc-hardware gpu` - NVIDIA GPU memory/load/temp
- `cc-hardware disk` - Per-drive storage info
- `cc-hardware os` - OS name, version, architecture
- `cc-hardware network` - Network interfaces and IPs
- `cc-hardware battery` - Battery charge and status

**Options:**
- `--json, -j` - Output as JSON
- `--version, -v` - Show version

**Notes:**
- GPU info requires NVIDIA GPU with drivers installed
- Battery info only available on laptops

---

## cc-outlook

Outlook CLI with email and calendar support.

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

# Search
cc-outlook search "project update"

# Calendar
cc-outlook calendar events                           # Next 7 days
cc-outlook calendar events -d 14                     # Next 14 days
cc-outlook calendar events --start 2025-06-01 --end 2025-12-31  # Date range
cc-outlook calendar events --start 2025-01-01 -d 365            # Past year
cc-outlook calendar today                            # Today's agenda
cc-outlook calendar week                             # This week (Mon-Sun)
cc-outlook calendar search "standup"                 # Search by subject
cc-outlook calendar search "review" --start 2025-01-01 --end 2025-06-30
cc-outlook calendar freebusy "john@company.com"      # Check availability
cc-outlook calendar freebusy "a@co.com,b@co.com" -d 2026-03-05
cc-outlook calendar forward "EVENT_ID" -t "colleague@company.com" -m "FYI"
cc-outlook calendar create -s "Meeting" -d 2024-12-25 -t 14:00

# Profile
cc-outlook profile
```

---

## cc-youtube-info

Extract transcripts, metadata, and chapters from YouTube videos.

```bash
# Get video metadata
cc-youtube-info info "https://www.youtube.com/watch?v=VIDEO_ID"

# Download transcript
cc-youtube-info transcript URL
cc-youtube-info transcript URL -o transcript.txt

# Download as SRT subtitles
cc-youtube-info transcript URL --format srt -o captions.srt

# List available languages
cc-youtube-info languages URL

# Get chapters
cc-youtube-info chapters URL

# Output as JSON
cc-youtube-info info URL --json
```

**Options:**
- `-o, --output` - Output file
- `-l, --lang` - Language code (default: en)
- `-f, --format` - txt, srt, or vtt
- `--json` - Output as JSON
- `--no-timestamps` - Remove timestamps

---

## cc-crawl4ai

AI-ready web crawler: crawl pages to clean markdown for LLM/RAG workflows.

```bash
# Crawl a URL
cc-crawl4ai crawl "https://example.com"
cc-crawl4ai crawl URL -o page.md

# Fit markdown (noise filtered)
cc-crawl4ai crawl URL --fit

# Batch crawl from file
cc-crawl4ai batch urls.txt -o ./output/

# Stealth mode (evade bot detection)
cc-crawl4ai crawl URL --stealth

# Wait for dynamic content
cc-crawl4ai crawl URL --wait-for ".content-loaded"

# Scroll full page (infinite scroll)
cc-crawl4ai crawl URL --scroll

# Extract specific CSS selector
cc-crawl4ai crawl URL --css "article.main"

# Take screenshot
cc-crawl4ai crawl URL --screenshot

# Authenticated sessions
cc-crawl4ai session create mysite -u "https://example.com/login" --interactive
cc-crawl4ai crawl URL --session mysite
```

---

## cc-browser

Persistent browser automation with workspace management.

```bash
# Launch browser with workspace
cc-browser start --workspace myworkspace

# Navigate
cc-browser navigate "https://example.com"

# Take screenshot
cc-browser screenshot -o page.png

# Execute JavaScript
cc-browser eval "document.title"

# Close browser
cc-browser close
```

**Note:** Runs as a daemon for persistent browser sessions across commands.

---

## cc-photos

Photo organization tool: scan directories, detect duplicates and screenshots, AI descriptions.

```bash
# Add a source directory
cc-photos source add "D:\Photos" --category private --label "Family" --priority 1

# List sources
cc-photos source list

# Remove a source
cc-photos source remove "Family"

# Scan all sources
cc-photos scan

# Scan specific source
cc-photos scan --source "Family"

# Find duplicates
cc-photos dupes

# Auto-remove duplicates (keeps highest priority)
cc-photos dupes --cleanup

# Interactive duplicate review
cc-photos dupes --review

# List by category
cc-photos list --category private

# List screenshots
cc-photos list --screenshots

# AI analysis (generate descriptions)
cc-photos analyze
cc-photos analyze --limit 50
cc-photos analyze --provider openai

# Search descriptions
cc-photos search "beach vacation"

# Statistics
cc-photos stats
```

**Categories:** private, work, other

**Priority:** Lower number = higher priority (keeps that copy when removing duplicates)

**Config:** `~/.cc-tools/config.json`

**Database:** `~/.cc-tools/photos.db` (configurable)

---

## cc-click

Windows UI automation for clicking, typing, and inspecting elements.

```bash
# Click at coordinates
cc-click click 100 200

# Type text
cc-click type "Hello World"

# Inspect element at position
cc-click inspect 100 200

# List windows
cc-click windows

# Focus window by title
cc-click focus "Notepad"
```

---

## cc-comm-queue

CLI tool for adding content to the Communication Manager approval queue.

```bash
# Add LinkedIn post
cc-comm-queue add linkedin post "Process mining trends for 2024..." \
    --persona mindzie \
    --tags "process mining,trends"

# Add LinkedIn comment
cc-comm-queue add linkedin comment "Great insights!" \
    --persona personal \
    --context-url "https://linkedin.com/posts/..."

# Add email
cc-comm-queue add email email "Hi Sarah, following up..." \
    --persona mindzie \
    --email-to "sarah@techcorp.com" \
    --email-subject "Following up from Summit"

# Add Reddit post
cc-comm-queue add reddit post "How we reduced processing time..." \
    --persona personal \
    --reddit-subreddit "r/processimprovement" \
    --reddit-title "Case Study: 70% reduction"

# Add from JSON file
cc-comm-queue add-json content.json

# Add from stdin
cat content.json | cc-comm-queue add-json -

# List pending items
cc-comm-queue list --status pending

# Show queue stats
cc-comm-queue status

# Show specific item
cc-comm-queue show abc123

# JSON output (for agents)
cc-comm-queue add linkedin post "Hello" --json

# Configuration
cc-comm-queue config show
cc-comm-queue config set queue_path "D:/path/to/content"
cc-comm-queue config set default_persona mindzie
```

**Platforms:** linkedin, twitter, reddit, youtube, email, blog

**Types:** post, comment, reply, message, article, email

**Personas:**
- `mindzie` - CTO of mindzie
- `consulting` - Consulting services persona
- `personal` - Personal

**Options:**
- `--persona, -p` - Persona to use
- `--destination, -d` - Where to post (URL)
- `--context-url, -c` - What we're responding to
- `--tags, -t` - Comma-separated tags
- `--notes, -n` - Notes for reviewer
- `--json` - JSON output for agents
- `--linkedin-visibility` - public or connections
- `--reddit-subreddit` - Target subreddit
- `--reddit-title` - Reddit post title
- `--email-to` - Recipient email
- `--email-subject` - Email subject

**Config:** `~/.cc-tools/config.json` (comm_manager section)

---

## cc-computer

AI desktop automation agent using TriSight 3-tier visual detection (98.9% element clickability).

```bash
# Run a task in CLI mode
cc-computer "Open Notepad and type Hello World"

# Interactive REPL mode
cc-computer

# Launch GUI mode
cc-computer-gui
```

**Features:**
- Screenshot-in-the-loop verification after every action
- TriSight 3-tier detection: UI Automation + OCR + Pixel Analysis
- Evidence chain logging with timestamps and screenshots
- Semantic Kernel LLM orchestration

**How it works:**
1. Takes a screenshot
2. Detects UI elements (buttons, text fields, etc.)
3. Overlays numbered bounding boxes on screenshot
4. LLM sees annotated screenshot + element list
5. LLM issues action (click, type, shortcut)
6. Agent executes action, captures new screenshot
7. Loop until task complete

**Configuration:** Edit `appsettings.json` in install directory

```json
{
  "LLM": {
    "ModelId": "gpt-5.2",
    "ApiKey": "your-key-or-set-OPENAI_API_KEY"
  },
  "Desktop": {
    "CcClickPath": "cc-click\\cc-click.exe"
  }
}
```

**Dependencies:**
- cc-click (for UI automation actions)
- cc-trisight (shared detection library)

---

## cc-image

Image generation and analysis using OpenAI.

```bash
# Generate image from prompt
cc-image generate "A sunset over mountains" -o sunset.png

# Analyze/describe image
cc-image describe image.png

# OCR - extract text from image
cc-image ocr screenshot.png
```

---

## cc-voice

Text-to-speech using OpenAI TTS.

```bash
# Convert text to speech
cc-voice speak "Hello, world!" -o hello.mp3

# Use different voice
cc-voice speak "Hello" -o hello.mp3 --voice nova
```

**Voices:** alloy, echo, fable, nova, onyx, shimmer

---

## cc-whisper

Audio transcription using OpenAI Whisper.

```bash
# Transcribe audio
cc-whisper transcribe audio.mp3
cc-whisper transcribe audio.mp3 -o transcript.txt
```

---

## cc-video

Video utilities.

```bash
# Extract audio from video
cc-video extract-audio video.mp4 -o audio.mp3

# Get video info
cc-video info video.mp4
```

---

## cc-docgen

Generate C4 architecture diagrams from YAML manifest files.

```bash
# Generate diagrams
cc-docgen generate
cc-docgen generate --manifest ./docs/cencon/architecture_manifest.yaml
cc-docgen generate --output ./docs/ --format svg

# Validate manifest only
cc-docgen validate
```

**Output:** context.png and container.png (C4 Level 1 and Level 2 diagrams)

**Options:**
- `-m, --manifest` - Path to architecture_manifest.yaml
- `-o, --output` - Output directory
- `-f, --format` - png or svg (default: png)
- `-v, --verbose` - Verbose output

**Requires:** Graphviz installed and on PATH

---

## cc-linkedin

LinkedIn automation CLI with human-like delays. Communicates through cc-browser daemon.

```bash
# Status
cc-linkedin status
cc-linkedin whoami

# Feed
cc-linkedin feed --limit 10
cc-linkedin post URL

# Profile
cc-linkedin profile username
cc-linkedin connections --limit 20

# Messages
cc-linkedin messages --unread
cc-linkedin message username --text "Hello"

# Search
cc-linkedin search "query" --type people
cc-linkedin search "query" --type companies

# Post creation
cc-linkedin create "Post content here"
cc-linkedin create "Post with image" --image photo.png
```

**Note:** NEVER use cc-browser directly with LinkedIn. Always use cc-linkedin which has human-like delays built in.

---

## cc-reddit

Reddit automation CLI with human-like delays. Uses browser automation with random jitter.

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
cc-reddit post URL

# Interact
cc-reddit comment URL "Comment text"
cc-reddit reply URL "Reply text"
```

**Note:** NEVER use cc-browser directly with Reddit. Always use cc-reddit.

---

## cc-spotify

Spotify CLI via browser automation. Controls Spotify Web Player (open.spotify.com) through cc-browser.

```bash
# Setup
cc-spotify config --workspace edge-personal
# Then: cc-browser start --workspace edge-personal
# Navigate to open.spotify.com and log in (one-time)

# Status
cc-spotify status
cc-spotify now

# Playback
cc-spotify play
cc-spotify pause
cc-spotify next
cc-spotify prev

# Controls
cc-spotify shuffle --on
cc-spotify shuffle --off
cc-spotify repeat off                    # off, context, track
cc-spotify volume 75                     # 0-100
cc-spotify like                          # Heart current track

# Browse
cc-spotify search "Miles Davis"
cc-spotify playlists                     # List sidebar playlists
cc-spotify playlist "Chill Vibes"        # Play by name
cc-spotify queue                         # Show queue
cc-spotify goto URL                      # Navigate to Spotify URL

# Vault recommendations
cc-spotify recommend
cc-spotify recommend --mood "chill jazz"

# Config
cc-spotify config --workspace NAME       # Set default workspace
cc-spotify config --show                 # Show config
```

**Global options:** `--workspace, -w` (override per-call), `--format text|json`, `--verbose`

**Note:** Requires cc-browser daemon running with Spotify open. Premium recommended for full playback control.

---

## cc-setup

Windows installer for the cc-tools suite.

```bash
# Run installer (no arguments)
cc-director-setup
```

Downloads tools from GitHub releases, configures PATH, installs Claude Code skill. No admin privileges required.

---

## cc-trisight

Three-tier UI element detection for Windows. Combines UI Automation + OCR + pixel analysis.

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

**Options:**
- `--window, -w` - Target window title (substring match)
- `--tiers` - Detection tiers: uia,ocr,pixel (default: all)
- `--depth, -d` - Max UIA tree depth (default: 15)
- `--annotate` - Generate annotated screenshot with numbered boxes
- `--output, -o` - Output path for annotated image

---

## cc-vault

Personal data vault with contacts, documents, tasks, goals, ideas, and RAG search.

```bash
# Search and ask
cc-vault search "query"
cc-vault ask "question"

# Contacts
cc-vault contacts list
cc-vault contacts add "Name" -e "email@example.com"
cc-vault contacts get <id>

# Documents
cc-vault docs list
cc-vault docs get <id>
cc-vault docs import file.pdf

# Tasks, goals, ideas
cc-vault tasks list
cc-vault goals list
cc-vault ideas list

# Contact lists
cc-vault lists list                                  # List all lists
cc-vault lists create "Name" -d "Description" -t type  # Create a list
cc-vault lists show "Name"                           # Show list members
cc-vault lists rename "Old Name" "New Name"          # Rename a list
cc-vault lists update "Name" -d "New desc" -t type   # Update metadata
cc-vault lists copy "Source" "Dest"                  # Duplicate with members
cc-vault lists add "Name" -c 42                      # Add contact by ID
cc-vault lists add "Name" -q "company = 'Acme'"      # Add by query
cc-vault lists remove "Name" -c 42                   # Remove contact by ID
cc-vault lists remove "Name" -q "company = 'Acme'"   # Remove by query
cc-vault lists export "Name" -f json                 # Export as JSON or CSV
cc-vault lists delete "Name" -y                      # Delete a list
```

**Storage:** `%LOCALAPPDATA%\cc-myvault` or `D:/Vault` (legacy)

---

## Environment Variables

```bash
# Required for AI-powered tools
set OPENAI_API_KEY=your-key-here
```

---

## Requirements Summary

| Tool | Requirements |
|------|--------------|
| cc-browser | Node.js, Playwright |
| cc-click | Windows, .NET runtime |
| cc-brandingrecommendations | Node.js, cc-websiteaudit JSON output |
| cc-comm-queue | None |
| cc-computer | Windows, .NET runtime, OPENAI_API_KEY |
| cc-crawl4ai | `playwright install chromium` |
| cc-docgen | Graphviz (`dot` on PATH) |
| cc-excel | None |
| cc-gmail | OAuth credentials from Google Cloud Console |
| cc-hardware | None (NVIDIA drivers for GPU info) |
| cc-image | OPENAI_API_KEY |
| cc-linkedin | Playwright browsers, cc-browser |
| cc-markdown | Chrome/Chromium (auto-detected) |
| cc-outlook | Azure App Registration with OAuth |
| cc-photos | OPENAI_API_KEY (for AI analysis) |
| cc-powerpoint | None |
| cc-reddit | Playwright browsers, cc-browser |
| cc-spotify | cc-browser workspace with Spotify open |
| cc-setup | None (internet for GitHub downloads) |
| cc-transcribe | FFmpeg in PATH, OPENAI_API_KEY |
| cc-trisight | Windows, .NET runtime |
| cc-vault | None |
| cc-video | FFmpeg in PATH |
| cc-voice | OPENAI_API_KEY |
| cc-websiteaudit | Node.js, Chrome/Chromium |
| cc-whisper | OPENAI_API_KEY |
| cc-youtube-info | None |

---

## cc-websiteaudit

Comprehensive website auditing tool that crawls sites and grades them across technical SEO, on-page SEO, security, structured data, and AI readiness. Produces reports in console, JSON, HTML, Markdown, and PDF formats.

```bash
# Basic audit (console output)
cc-websiteaudit https://example.com

# Save as PDF report
cc-websiteaudit example.com -o report.pdf

# JSON output (for cc-brandingrecommendations)
cc-websiteaudit example.com --format json -o audit.json

# Markdown report
cc-websiteaudit example.com --format markdown -o audit.md

# Custom crawl settings
cc-websiteaudit example.com --pages 50 --depth 4 --verbose

# Run specific modules only
cc-websiteaudit example.com --modules technical-seo,security

# Quiet mode (grade only)
cc-websiteaudit example.com --quiet
```

**Options:**

| Option | Description | Default |
|--------|-------------|---------|
| `<url>` | Website URL to audit (required) | - |
| `-o, --output <path>` | Output file path (format auto-detected from extension) | stdout |
| `--format <type>` | Output format: console, json, html, markdown, pdf | console |
| `--modules <list>` | Comma-separated modules to run | all |
| `--pages <number>` | Max pages to crawl | 25 |
| `--depth <number>` | Max crawl depth | 3 |
| `--verbose` | Show detailed progress | false |
| `--quiet` | Only show final grade | false |

**Analyzer modules:**

- **technical-seo** (20%) - robots.txt, sitemaps, canonicals, HTTPS, redirects, status codes, crawl depth, URL structure
- **on-page-seo** (20%) - title tags, meta descriptions, heading hierarchy, image alt text, internal linking, content length, duplicate content, Open Graph
- **security** (10%) - HSTS, CSP, X-Content-Type-Options, X-Frame-Options, Referrer-Policy, Permissions-Policy
- **structured-data** (10%) - JSON-LD, Organization/Article/FAQ/Breadcrumb schemas, validity
- **ai-readiness** (20%) - /llms.txt, AI crawler access, content citability, passage structure, semantic HTML, entity clarity, question coverage

**Grades:** A+ (97+) through F (<60). Auto-detects Cloudflare/SPA sites and switches to browser rendering.

---

## cc-brandingrecommendations

Reads cc-websiteaudit JSON output and produces a prioritized, week-by-week action plan with research-backed recommendations across SEO, security, structured data, AI readiness, content strategy, social media, and backlinks.

```bash
# Basic usage (console output)
cc-brandingrecommendations --audit report.json

# JSON output
cc-brandingrecommendations --audit report.json --format json

# Markdown report (auto-detected from .md extension)
cc-brandingrecommendations --audit report.json -o plan.md

# With context options
cc-brandingrecommendations --audit report.json --budget high --industry saas --keywords "project management,team collaboration" --competitors "asana.com,monday.com"

# Verbose mode
cc-brandingrecommendations --audit report.json --verbose
```

**Options:**

| Option | Description | Default |
|--------|-------------|---------|
| `--audit <path>` | Path to cc-websiteaudit JSON report (required) | - |
| `-o, --output <path>` | Output file path (auto-detects format from extension) | stdout |
| `--format <type>` | Output format: console, json, markdown | console |
| `--budget <level>` | Weekly budget: low (5h), medium (10h), high (20h) | medium |
| `--industry <type>` | Industry vertical for tailored recommendations | - |
| `--keywords <list>` | Comma-separated target keywords | - |
| `--competitors <list>` | Comma-separated competitor domains | - |
| `--verbose` | Show detailed progress | false |

**Priority classification (Eisenhower matrix):**

- **Quick Win**: High impact (4-5), low effort (1-2) -> Weeks 1-2
- **Strategic**: High impact (4-5), high effort (3-5) -> Weeks 5-8
- **Easy Fill**: Low impact (1-3), low effort (1-2) -> Weeks 3-4
- **Deprioritize**: Low impact (1-3), high effort (3-5) -> Weeks 9-12

**Workflow:** Run cc-websiteaudit first, then feed its JSON output to cc-brandingrecommendations:

```bash
cc-websiteaudit example.com --format json -o audit.json
cc-brandingrecommendations --audit audit.json -o plan.md
cc-markdown plan.md -o plan.pdf --theme boardroom
```

---

## Source Repository

GitHub: https://github.com/cc-director/cc-director
